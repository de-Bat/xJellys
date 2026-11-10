package com.xjellys.tvoverlay;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Log;

import java.net.HttpURLConnection;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

import com.xjellys.tvoverlay.logic.OverlayEvents;
import com.xjellys.tvoverlay.logic.PlayPosition;
import com.xjellys.tvoverlay.logic.PollPlan;
import com.xjellys.tvoverlay.logic.PreloadCache;
import com.xjellys.tvoverlay.logic.SessionPicker;
import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.render.ImageCache;
import com.xjellys.tvoverlay.render.OverlayManager;
import com.xjellys.tvoverlay.render.OverlayWindow;

/**
 * Foreground service: follows the Jellyfin player session on this box (/Sessions every 5 s, only for its deviceId,
 * sessionId and position), heartbeats the JellyTvOverlay plugin every 30 s, and long-polls it for overlay ops on
 * its own thread. Ops are applied on the main thread; events and remote-control calls go out on background
 * executors, so the UI never waits on the network.
 */
public class OverlayService extends Service {
    static volatile String status = "Not running";
    /** For MainActivity's display only; the instance itself checks {@link #destroyed}. */
    static volatile boolean alive;

    static final String DENIED = "This box isn't allowed to use the overlay (check the JellyTvOverlay plugin settings)";
    private static final String TAG = "JellyTvOverlay";
    private static final String CHANNEL = "overlay";
    private static final long FOLLOW_MS = 5000, BEAT_MS = 30000, NET_BACKOFF_MS = 2000, DENIED_BACKOFF_MS = 30000;
    private static final long SEEK_MS = 10000;

    private final Handler main = new Handler(Looper.getMainLooper());
    private HandlerThread followThread;
    private Handler follow;
    private ExecutorService events, control;
    private Prefs prefs;
    private Api api;

    /** Bumped when the followed device changes, overlays get switched off/on, or the service stops: old poll loops exit. */
    private final AtomicInteger gen = new AtomicInteger();
    /** This instance's liveness: set first in onDestroy, so a stopped instance never starts or applies anything. */
    private volatile boolean destroyed;
    /** The running poll loop (null = none); aborted on restart and destroy. */
    private volatile PollLoop loop;
    private volatile String device;
    private volatile String sessionId;
    private volatile boolean enabled = true;
    /** Set by the poll loop while it cannot poll (null = fine); shown in the status line. */
    private volatile String pollProblem;
    private final PlayPosition pos = new PlayPosition();

    // follow thread only
    private long lastBeat;
    private String beatDevice;

    // main thread only
    private OverlayManager overlays;
    private ImageCache images;
    private final PreloadCache preloads = new PreloadCache(32);

    static void start(Context ctx) {
        ctx.startForegroundService(new Intent(ctx, OverlayService.class));
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        prefs = new Prefs(this);
        api = Api.of(prefs);
        images = new ImageCache(api, 24 * 1024 * 1024);
        overlays = new OverlayManager(this, images, windows);
        followThread = new HandlerThread("jellytvoverlay-follow");
        followThread.start();
        follow = new Handler(followThread.getLooper());
        events = Executors.newSingleThreadExecutor(named("jellytvoverlay-events"));
        control = Executors.newSingleThreadExecutor(named("jellytvoverlay-control"));
        getSystemService(NotificationManager.class).createNotificationChannel(
            new NotificationChannel(CHANNEL, "JellyTvOverlay", NotificationManager.IMPORTANCE_LOW));
        alive = true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(1, notification("Starting"));
        follow.removeCallbacks(tick);
        follow.post(tick);
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        destroyed = true;
        alive = false;
        gen.incrementAndGet();
        PollLoop l = loop;
        if (l != null) l.abort();
        follow.removeCallbacksAndMessages(null);
        followThread.quitSafely();
        events.shutdownNow();
        control.shutdownNow();
        overlays.closeAll();
        preloads.clear();
        images.shutdown();
        status = "Stopped";
        super.onDestroy();
    }

    // ---- session follow and heartbeat (follow thread) ----

    private final Runnable tick = new Runnable() {
        @Override public void run() {
            if (destroyed) return;
            try {
                followOnce();
            } catch (Exception e) {
                Log.w(TAG, "session poll failed: " + e.getClass().getSimpleName());
                setStatus("Can't reach the server (retrying)");
            }
            if (!destroyed) follow.postDelayed(this, FOLLOW_MS);
        }
    };

    private void followOnce() throws Exception {
        if (!prefs.signedIn()) {
            setStatus("Not signed in. Open JellyTvOverlay to sign in.");
            stopSelf();
            return;
        }
        if (!Settings.canDrawOverlays(this)) {
            setStatus("Overlay permission missing. Open JellyTvOverlay and press Allow overlay.");
            follow(null);
            return;
        }

        JSONArray list;
        try {
            list = api.sessions();
        } catch (Api.HttpError e) {
            if (e.code == 401) {
                expired();
                return;
            }
            throw e;
        }
        JSONObject s = SessionPicker.pick(list, prefs.deviceId(), prefs.watch(), prefs.deviceName());
        String dev = s == null ? null : s.optString("DeviceId", "");
        if (dev != null && dev.isEmpty()) dev = null;
        sessionId = s == null ? null : s.optString("Id", null);
        // beat before switching devices, so a new device's poll loop only starts when it is enabled
        long now = SystemClock.elapsedRealtime();
        if (dev != null && (lastBeat == 0 || now - lastBeat >= BEAT_MS || !dev.equals(beatDevice))) {
            lastBeat = now;
            beatDevice = dev;
            beat(dev);
            if (!prefs.signedIn()) return; // the heartbeat got a 401: signed out, stopping
        }
        follow(dev);
        sample(s); // after follow(): a device switch clears the old position
        if (dev == null) {
            setStatus("Waiting for Jellyfin on this box to play something");
            return;
        }

        String p = pollProblem;
        if (!enabled) {
            setStatus("Overlays are switched off for this box in the JellyTvOverlay plugin settings");
        } else if (p != null) {
            setStatus(p);
        } else {
            setStatus("Following " + s.optString("DeviceName") + " (" + s.optString("Client") + ")");
        }
    }

    /** Position for seek passthrough: the session's PlayState plus wall time while playing. */
    private void sample(JSONObject s) {
        JSONObject np = s == null ? null : s.optJSONObject("NowPlayingItem");
        JSONObject ps = s == null ? null : s.optJSONObject("PlayState");
        if (np == null || ps == null) {
            pos.clear();
            return;
        }
        pos.update(ps.optLong("PositionTicks", 0), ps.optBoolean("IsPaused", false), np.optLong("RunTimeTicks", 0),
            SystemClock.elapsedRealtime());
    }

    private void beat(String dev) {
        try {
            JSONObject hb = api.heartbeat(dev);
            boolean en = hb.optBoolean("enabled", true);
            if (en != enabled) {
                enabled = en;
                // off: close everything and stop polling (heartbeats go on); on again: poll from since=0.
                // For a device not followed yet, follow() starts it with this answer.
                if (dev.equals(device)) restart();
            }
        } catch (Api.HttpError e) {
            if (e.code == 401) {
                expired();
            } else if (e.code == 403) {
                pollProblem = DENIED;
            } else if (e.code == 404) {
                pollProblem = "The JellyTvOverlay plugin is not installed on the server";
            } else {
                Log.w(TAG, "heartbeat failed: HTTP " + e.code);
            }
        } catch (Exception e) {
            Log.w(TAG, "heartbeat failed: " + e.getClass().getSimpleName());
        }
    }

    /** Switches the long-poll to another player device (null = none): close everything, start over at since=0. */
    private void follow(String dev) {
        String cur = device;
        if (dev == null ? cur == null : dev.equals(cur)) return;
        device = dev;
        pos.clear();
        restart();
    }

    /** Ends the current poll loop, closes every window, and starts a new loop if a device is followed and enabled. */
    private void restart() {
        final int g = gen.incrementAndGet();
        PollLoop old = loop;
        loop = null;
        if (old != null) old.abort();
        pollProblem = null;
        if (destroyed) return;
        main.post(new Runnable() {
            @Override public void run() {
                if (destroyed) return;
                overlays.closeAll();
                preloads.clear();
            }
        });
        String dev = device;
        if (dev != null && enabled) {
            PollLoop pl = new PollLoop(g, dev);
            loop = pl;
            Thread t = new Thread(pl, "jellytvoverlay-poll");
            t.setDaemon(true);
            t.start();
        }
    }

    private synchronized void expired() {
        if (!prefs.signedIn()) return;
        setStatus("Sign-in expired. Open JellyTvOverlay and sign in again.");
        prefs.signOut();
        gen.incrementAndGet();
        PollLoop l = loop;
        loop = null;
        if (l != null) l.abort();
        main.post(new Runnable() {
            @Override public void run() {
                if (!destroyed) overlays.closeAll();
            }
        });
        stopSelf();
    }

    // ---- long-poll (its own thread per device) ----

    private final class PollLoop implements Runnable {
        private final int g;
        private final String dev;
        private final AtomicReference<HttpURLConnection> conn = new AtomicReference<HttpURLConnection>();

        PollLoop(int g, String dev) {
            this.g = g;
            this.dev = dev;
        }

        private boolean current() {
            return !destroyed && gen.get() == g;
        }

        /** Ends a blocked poll read now (the loop then sees it is no longer current and exits). */
        void abort() {
            Api.abort(conn);
        }

        @Override public void run() {
            long since = 0;
            while (current()) {
                long backoff = 0;
                try {
                    JSONObject r = api.poll(dev, since, conn);
                    if (!current()) break;
                    final PollPlan plan = PollPlan.of(r, since);
                    since = plan.seq;
                    pollProblem = null;
                    if (!plan.steps.isEmpty()) {
                        main.post(new Runnable() {
                            @Override public void run() { apply(plan, g); }
                        });
                    }
                } catch (Api.HttpError e) {
                    if (e.code == 401) {
                        expired();
                        break;
                    } else if (e.code == 403) {
                        problem(DENIED);
                        backoff = DENIED_BACKOFF_MS;
                    } else if (e.code == 404) {
                        problem("The JellyTvOverlay plugin is not installed on the server");
                        backoff = DENIED_BACKOFF_MS;
                    } else {
                        problem("Overlay server error (HTTP " + e.code + "), retrying");
                        backoff = NET_BACKOFF_MS;
                    }
                } catch (Exception e) {
                    // network error or a garbled answer: keep since, try again shortly
                    Log.w(TAG, "poll failed: " + e.getClass().getSimpleName());
                    problem("Can't reach the server (retrying)");
                    backoff = NET_BACKOFF_MS;
                }
                if (backoff > 0) {
                    try {
                        Thread.sleep(backoff);
                    } catch (InterruptedException ie) {
                        break;
                    }
                }
            }
        }

        private void problem(String s) {
            if (!current()) return;
            pollProblem = s;
            setStatus(s);
        }
    }

    /** Main thread: one Poll response, in order. Stale plans (device changed meanwhile) are dropped. */
    private void apply(PollPlan plan, int g) {
        if (destroyed || gen.get() != g) return;
        for (PollPlan.Step st : plan.steps) {
            switch (st.kind) {
                case RESET:
                    overlays.closeAllExcept(st.keep);
                    break;
                case CLOSE:
                    overlays.close(st.id);
                    break;
                case SHOW:
                    show(st.layout);
                    break;
                default:
                    break;
            }
        }
    }

    private void show(JSONObject json) {
        Layout l = preloads.take(json);
        try {
            if (l == null) l = Layout.parse(json);
        } catch (JSONException e) {
            Log.w(TAG, "layout dropped: " + e.getMessage());
            return;
        }
        preloads.addFrom(json, l);
        try {
            overlays.show(l);
        } catch (RuntimeException e) {
            Log.w(TAG, "show failed: " + e.getClass().getSimpleName());
            setStatus("Could not show an overlay (overlay permission?)");
        }
    }

    // ---- window callbacks (main thread) -> network on executors ----

    private final OverlayWindow.Listener windows = new OverlayWindow.Listener() {
        @Override public void onAction(Layout l, String action, String elementId, JSONObject values) {
            String dev = device;
            if (dev != null) send(OverlayEvents.action(dev, l.id, action, elementId, values));
        }

        @Override public void onClosed(Layout l, String reason) {
            String dev = device;
            if (dev != null) send(OverlayEvents.closed(dev, l.id, reason));
        }

        @Override public void onPassthrough(final String key) {
            run(control, new Runnable() {
                @Override public void run() { passthrough(key); }
            });
        }
    };

    private void send(final JSONObject body) {
        run(events, new Runnable() {
            @Override public void run() {
                try {
                    api.event(body);
                } catch (Api.HttpError e) {
                    // 403: the layout is gone on the host already; nothing to do
                    Log.w(TAG, "event not taken: HTTP " + e.code);
                } catch (Exception e) {
                    Log.w(TAG, "event failed: " + e.getClass().getSimpleName());
                }
            }
        });
    }

    /** Media keys a passthrough layout hands on to the player. */
    private void passthrough(String key) {
        String sid = sessionId;
        if (sid == null || sid.isEmpty()) return;
        long now = SystemClock.elapsedRealtime();
        try {
            if ("play".equals(key)) {
                api.playstate(sid, "PlayPause");
                pos.togglePaused(now);
            } else if ("stop".equals(key)) {
                api.stop(sid);
            } else if ("left".equals(key) || "right".equals(key)) {
                long t = pos.seekTarget(now, "left".equals(key) ? -SEEK_MS : SEEK_MS);
                if (t < 0) return;
                api.seek(sid, t);
                pos.seekedTo(t, now);
            }
        } catch (Exception e) {
            Log.w(TAG, "remote control '" + key + "' failed: " + e.getClass().getSimpleName());
        }
    }

    private static void run(ExecutorService ex, Runnable r) {
        try {
            ex.execute(r);
        } catch (RejectedExecutionException ignored) {
            // service stopping
        }
    }

    private static ThreadFactory named(final String name) {
        return new ThreadFactory() {
            @Override public Thread newThread(Runnable r) {
                Thread t = new Thread(r, name);
                t.setDaemon(true);
                return t;
            }
        };
    }

    // ---- status ----

    private synchronized void setStatus(String s) {
        if (s.equals(status)) return;
        if (destroyed) return;
        status = s;
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.notify(1, notification(s));
    }

    private Notification notification(String text) {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class),
            PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, CHANNEL)
            .setContentTitle("JellyTvOverlay")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentIntent(open)
            .setOngoing(true)
            .build();
    }
}
