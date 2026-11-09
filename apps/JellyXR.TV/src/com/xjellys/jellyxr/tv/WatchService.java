package com.xjellys.jellyxr.tv;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Follows the Jellyfin playback session on this box (polls /Sessions once a second) and shows
 * the cast panel while it is paused. Tells the JellyXR plugin it is here, so the plugin stops
 * sending text toasts to that device.
 */
public class WatchService extends Service {
    static final String ACTION_TEST = "com.xjellys.jellyxr.tv.TEST";
    static volatile String status = "Not running";
    static volatile boolean alive;

    private static final String TAG = "JellyXR";
    private static final String CHANNEL = "watch";
    private static final long POLL_MS = 1000, IDLE_POLL_MS = 3000, ERROR_MS = 5000, BEAT_MS = 30000;

    private final Handler main = new Handler(Looper.getMainLooper());
    private HandlerThread thread;
    private Handler worker;
    private CastPanel panel;
    private Prefs prefs;

    private boolean wasPaused;
    private String pausedItem;
    private int pauseSeq;
    private long lastBeat;
    private String lastBeatDevice;
    private boolean serverPanelOn = true;
    private final Map<String, Bitmap> faces = new HashMap<>();

    static void start(Context ctx) {
        ctx.startForegroundService(new Intent(ctx, WatchService.class));
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        prefs = new Prefs(this);
        panel = new CastPanel(this);
        thread = new HandlerThread("jellyxr-poll");
        thread.start();
        worker = new Handler(thread.getLooper());
        alive = true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(1, notification("Starting"));
        if (intent != null && ACTION_TEST.equals(intent.getAction())) {
            main.post(new Runnable() {
                @Override public void run() { showTest(); }
            });
        }
        worker.removeCallbacks(poll);
        worker.post(poll);
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        alive = false;
        worker.removeCallbacksAndMessages(null);
        thread.quitSafely();
        main.post(new Runnable() {
            @Override public void run() { panel.hide(); }
        });
        status = "Stopped";
        super.onDestroy();
    }

    private final Runnable poll = new Runnable() {
        @Override
        public void run() {
            long delay = POLL_MS;
            try {
                delay = tick();
            } catch (Exception e) {
                setStatus("Server error: " + e.getMessage() + " (retrying)");
                Log.w(TAG, "poll failed", e);
                delay = ERROR_MS;
            }
            if (alive) worker.postDelayed(this, delay);
        }
    };

    /** One poll. Returns the delay until the next one. */
    private long tick() throws Exception {
        if (!prefs.signedIn()) {
            setStatus("Not signed in. Open JellyXR TV to sign in.");
            stopSelf();
            return IDLE_POLL_MS;
        }
        if (!Settings.canDrawOverlays(this)) {
            setStatus("Overlay permission missing. Open JellyXR TV and press Allow overlay.");
            return ERROR_MS;
        }

        Api api = Api.of(prefs);
        JSONObject s;
        try {
            s = pick(api.sessions(), prefs);
        } catch (Api.HttpError e) {
            if (e.code == 401) {
                setStatus("Sign-in expired. Open JellyXR TV and sign in again.");
                prefs.signOut();
                stopSelf();
                return IDLE_POLL_MS;
            }
            throw e;
        }

        if (s == null) {
            changePause(false, null, 0);
            setStatus("Waiting for Jellyfin on this box to play something");
            return IDLE_POLL_MS;
        }

        String device = s.optString("DeviceId");
        long now = System.currentTimeMillis();
        if (now - lastBeat > BEAT_MS || !device.equals(lastBeatDevice)) {
            lastBeat = now;
            lastBeatDevice = device;
            try {
                serverPanelOn = api.heartbeat(device);
            } catch (Api.HttpError e) {
                // 404: JellyXR plugin too old (before 1.4) or missing; cards will tell
                serverPanelOn = e.code != 403;
            }
        }

        JSONObject item = s.optJSONObject("NowPlayingItem");
        JSONObject ps = s.optJSONObject("PlayState");
        boolean paused = item != null && ps != null && ps.optBoolean("IsPaused");
        String label = s.optString("DeviceName") + " (" + s.optString("Client") + ")";
        if (item == null) {
            setStatus("Watching " + label + ": nothing playing");
        } else if (!serverPanelOn) {
            setStatus("Watching " + label + ": panel is off in the JellyXR plugin settings");
        } else {
            setStatus("Watching " + label + ": " + (paused ? "paused" : "playing") + " " + item.optString("Name"));
        }
        changePause(paused && serverPanelOn, item == null ? null : item.optString("Id"),
            ps == null ? 0 : ps.optLong("PositionTicks"));
        return item == null ? IDLE_POLL_MS : POLL_MS;
    }

    private void changePause(boolean paused, String itemId, long ticks) throws Exception {
        if (paused && (!wasPaused || !itemId.equals(pausedItem))) {
            wasPaused = true;
            pausedItem = itemId;
            final int seq = ++pauseSeq;
            final List<CastPanel.Card> cards = new ArrayList<>();
            JSONObject d = Api.of(prefs).cards(itemId, ticks);
            if (d == null) return; // 204: nothing to show, or the panel is off
            JSONArray arr = d.optJSONArray("Cards");
            int facePx = Math.round(64 * getResources().getDisplayMetrics().density);
            for (int i = 0; arr != null && i < arr.length(); i++) {
                JSONObject c = arr.getJSONObject(i);
                CastPanel.Card card = new CastPanel.Card(c.optString("Actor"), str(c, "Character"), str(c, "Info"), c.optBoolean("OnScreen"));
                String pid = str(c, "PersonId");
                if (pid != null && c.optBoolean("HasImage")) card.face = face(pid, facePx);
                cards.add(card);
            }
            if (cards.isEmpty()) return;
            final String header = d.optString("Header");
            final int secs = d.optInt("OverlaySeconds");
            main.post(new Runnable() {
                @Override public void run() {
                    if (seq != pauseSeq || !wasPaused) return; // resumed while loading
                    try {
                        panel.show(header, cards, secs);
                    } catch (Exception e) {
                        setStatus("Could not draw the panel: " + e.getMessage());
                    }
                }
            });
        } else if (!paused && wasPaused) {
            wasPaused = false;
            pausedItem = null;
            pauseSeq++;
            main.post(new Runnable() {
                @Override public void run() { panel.hide(); }
            });
        }
    }

    private Bitmap face(String personId, int px) {
        Bitmap b = faces.get(personId);
        if (b == null) {
            b = Api.of(prefs).personImage(personId, px);
            if (b != null) {
                if (faces.size() > 80) faces.clear();
                faces.put(personId, b);
            }
        }
        return b;
    }

    private static String str(JSONObject o, String key) {
        return o.isNull(key) ? null : o.optString(key, null);
    }

    /**
     * The session to follow: the one the user picked, else a Jellyfin Android TV session on this box
     * (same device name), else any other Android TV session, else any other session playing video.
     */
    static JSONObject pick(JSONArray list, Prefs p) {
        String me = p.deviceId();
        String want = p.watch();
        String box = p.deviceName();
        JSONObject sameBox = null, anyTv = null, any = null;
        for (int i = 0; i < list.length(); i++) {
            JSONObject s = list.optJSONObject(i);
            if (s == null) continue;
            String id = s.optString("DeviceId");
            if (id.equals(me)) continue;
            if (!want.isEmpty()) {
                if (id.equals(want)) return s;
                continue;
            }
            boolean tv = s.optString("Client").toLowerCase().contains("android tv");
            boolean playing = s.optJSONObject("NowPlayingItem") != null;
            if (tv && s.optString("DeviceName").equalsIgnoreCase(box)) {
                if (sameBox == null || playing) sameBox = s;
            } else if (tv && playing && anyTv == null) {
                anyTv = s;
            } else if (playing && any == null && isVideo(s)) {
                any = s;
            }
        }
        return sameBox != null ? sameBox : anyTv != null ? anyTv : any;
    }

    private static boolean isVideo(JSONObject s) {
        String t = s.optJSONObject("NowPlayingItem").optString("Type");
        return "Movie".equals(t) || "Episode".equals(t);
    }

    private void showTest() {
        List<CastPanel.Card> cards = new ArrayList<>();
        cards.add(new CastPanel.Card("Test Actor One", "Lead role", "34 min on screen, next at 1:12:40", true));
        cards.add(new CastPanel.Card("Test Actor Two", "Friend", "21 min on screen", true));
        for (int i = 3; i <= 14; i++) cards.add(new CastPanel.Card("Test Actor " + i, "Role " + i, null, false));
        if (!Settings.canDrawOverlays(this)) {
            setStatus("Test failed: overlay permission missing");
            return;
        }
        try {
            panel.show("Cast", cards, 12);
        } catch (Exception e) {
            setStatus("Test failed: " + e.getMessage());
        }
    }

    private void setStatus(String s) {
        if (s.equals(status)) return;
        status = s;
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.notify(1, notification(s));
    }

    private Notification notification(String text) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "JellyXR cast panel", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class),
            PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, CHANNEL)
            .setContentTitle("JellyXR TV")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentIntent(open)
            .setOngoing(true)
            .build();
    }
}
