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
import android.widget.Toast;

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

    // Jellycap recap text cards
    private static final long RECAP_ASK_MS = 60000, RECAP_GRACE_MS = 6000, HOLD_RETRY_MS = 3000, HOLD_GIVE_UP_MS = 20000, CAST_QUIET_MS = 4000;
    private RecapCard recapCard;
    private boolean jellycapOn;
    private String recapItem, recapDevice;
    /** Keep asking Jellycap until then while it prepares the cards for recapItem. */
    private long recapAskUntil, recapSince;
    /** Ready cards for recapItem, null = none. */
    private JSONArray recapCards;
    /** Pause mode (text recap over the paused episode) or timed mode (cards over the recap video). */
    private boolean recapPause;
    /** Pause mode: first / last Hold request, and when the pause was seen (0 = not yet). */
    private long holdFirst, holdLast, holdT0;
    private int recapShown = -1;
    /** No cast panel until then: the episode is still reported paused right after the recap resumed it. */
    private long castQuietUntil;

    // JellySubSync box: pause, play and pause again within DOUBLE_PAUSE_MS opens it
    private static final long DOUBLE_PAUSE_MS = 4000, FAST_POLL_MS = 400, FAST_WINDOW_MS = 10000, SUB_CHECK_MS = 3000, SUB_GIVE_UP_MS = 25000;
    private SyncBox syncBox;
    private boolean rawPaused;
    private String rawItem;
    /** When playback last resumed / paused (0 = not recently). */
    private long resumedAt, pausedAt;
    /** Box up (worker thread view). */
    private volatile boolean syncOpen;
    private boolean syncApplying;
    /** What was playing when the box opened. */
    private String syncSession, syncItemId, syncSource;
    private long syncTicks;
    private int syncAudio, syncSub;
    private boolean syncCanSetSub;
    /** After a restart: the synced stream the player should be on, -1 = nothing to check. */
    private int pendingSub = -1;
    private long pendingSince;
    private boolean pendingSent;

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
        recapCard = new RecapCard(this);
        syncBox = new SyncBox(this);
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
            @Override public void run() { panel.hide(); recapCard.hide(); syncBox.hide(); }
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
            subSync(api, null, null, null, false, false, System.currentTimeMillis());
            endRecap(api);
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
            try {
                jellycapOn = api.jellycapHeartbeat(device);
            } catch (Exception e) {
                jellycapOn = false;
                Log.w(TAG, "jellycap heartbeat failed", e);
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
        boolean recapOwns = recap(api, device, item, ps, paused, now);
        if (recapCards != null) setStatus("Watching " + label + ": recap text cards for " + item.optString("Name"));
        boolean syncUp = subSync(api, s, item, ps, paused, recapOwns || recapCards != null, now);
        boolean castOk = !recapOwns && !syncUp && now >= castQuietUntil;
        changePause(paused && serverPanelOn && castOk, item == null ? null : item.optString("Id"),
            ps == null ? 0 : ps.optLong("PositionTicks"));
        if (item == null) return IDLE_POLL_MS;
        if (recapCards != null) return POLL_MS / 2;
        // poll faster around pauses so the double pause is seen
        boolean fast = paused && now - pausedAt < FAST_WINDOW_MS || resumedAt > 0 && now - resumedAt < DOUBLE_PAUSE_MS || pendingSub >= 0;
        return fast ? FAST_POLL_MS : POLL_MS;
    }

    /**
     * Jellycap recap text cards for the playing item. Pause mode: holds the episode paused while the cards
     * show one after another, then lets Jellycap resume it. Timed mode: shows each card over its clip of the
     * recap video, by playback position. Returns true while the recap owns the screen (no cast panel).
     */
    private boolean recap(Api api, String device, JSONObject item, JSONObject ps, boolean paused, long now) throws Exception {
        String id = item == null ? null : item.optString("Id", null);
        if (id == null || !id.equals(recapItem) || !device.equals(recapDevice)) {
            endRecap(api);
            recapItem = id;
            recapDevice = device;
            recapSince = now;
            recapAskUntil = id != null && jellycapOn ? now + RECAP_ASK_MS : 0;
        }
        if (id == null) return false;

        if (recapCards == null && now < recapAskUntil) {
            JSONObject o = api.recapOverlay(id, device);
            if (o == null) {
                // nothing for this item; the first seconds Jellycap may not have seen the playback start yet
                if (now - recapSince > RECAP_GRACE_MS) recapAskUntil = 0;
            } else if ("ready".equals(o.optString("Status"))) {
                recapAskUntil = 0;
                JSONArray cards = o.optJSONArray("Cards");
                if (cards != null && cards.length() > 0) {
                    recapCards = cards;
                    recapPause = "Pause".equals(o.optString("Mode"));
                    holdFirst = holdLast = holdT0 = 0;
                    recapShown = -1;
                }
            }
        }
        if (recapCards == null) return false;

        if (!recapPause) {
            showCard(cardAt(ps == null ? 0 : ps.optLong("PositionTicks") / 1e7));
            return false;
        }

        if (holdT0 == 0) {
            if (paused) {
                holdT0 = now;
            } else {
                if (holdFirst == 0) holdFirst = now;
                if (now - holdFirst > HOLD_GIVE_UP_MS) {
                    Log.w(TAG, "episode never paused for the recap cards; giving up");
                    finishPause(api, false);
                    return false;
                }
                if (now - holdLast >= HOLD_RETRY_MS) {
                    holdLast = now;
                    api.recapHold(id, device);
                }
                return true;
            }
        }

        if (!paused) {
            // the user pressed play: drop the cards, leave the episode playing
            finishPause(api, false);
            return false;
        }

        double t = (now - holdT0) / 1000.0;
        if (t >= recapEnd()) {
            finishPause(api, true);
            castQuietUntil = now + CAST_QUIET_MS;
            return true;
        }
        showCard(cardAt(t));
        return true;
    }

    /**
     * JellySubSync box. Opens on a double pause (pause, play, pause within DOUBLE_PAUSE_MS), closes when
     * playback goes on without it, and after a restart on the synced track checks the player really switched.
     * Returns true while the box is up (no cast panel then).
     */
    private boolean subSync(Api api, JSONObject s, JSONObject item, JSONObject ps, boolean paused, boolean recapBusy, long now) {
        String id = item == null ? null : item.optString("Id", null);
        boolean sameItem = id != null && id.equals(rawItem);
        if (paused && !rawPaused) {
            boolean dbl = sameItem && resumedAt > 0 && now - resumedAt <= DOUBLE_PAUSE_MS;
            pausedAt = now;
            resumedAt = 0;
            if (dbl && !syncOpen && !recapBusy && now >= castQuietUntil && pendingSub < 0) openSync(api, s, item, ps);
        } else if (!paused && rawPaused) {
            resumedAt = sameItem ? now : 0;
        }
        rawPaused = paused;
        rawItem = id;

        if (syncOpen && !syncApplying && (!paused || id == null || !id.equals(syncItemId))) {
            // played on (phone, other remote) or a new item: the box is stale
            syncOpen = false;
            main.post(new Runnable() {
                @Override public void run() { syncBox.hide(); }
            });
        }

        if (pendingSub >= 0) {
            if (now - pendingSince > SUB_GIVE_UP_MS) {
                pendingSub = -1;
                toast("Subtitles synced. If they did not change, pick the \"Synced\" track in the player's subtitle menu.");
            } else if (s != null && id != null && id.equals(syncItemId) && !paused && ps != null) {
                int cur = ps.has("SubtitleStreamIndex") ? ps.optInt("SubtitleStreamIndex", -1) : -1;
                if (cur == pendingSub) {
                    pendingSub = -1;
                } else if (syncCanSetSub && !pendingSent && now - pendingSince > SUB_CHECK_MS) {
                    // the player restarted on another track: ask it to switch
                    pendingSent = true;
                    try {
                        api.setSubtitle(s.optString("Id"), pendingSub);
                    } catch (Exception e) {
                        Log.w(TAG, "SetSubtitleStreamIndex failed", e);
                    }
                }
            }
        }
        return syncOpen;
    }

    private void openSync(Api api, JSONObject s, JSONObject item, JSONObject ps) {
        syncSession = s.optString("Id");
        syncItemId = item.optString("Id");
        syncSource = ps.optString("MediaSourceId", "");
        syncTicks = ps.optLong("PositionTicks");
        syncAudio = ps.has("AudioStreamIndex") ? ps.optInt("AudioStreamIndex", -1) : -1;
        syncSub = ps.has("SubtitleStreamIndex") ? ps.optInt("SubtitleStreamIndex", -1) : -1;
        syncCanSetSub = supports(s, "SetSubtitleStreamIndex");

        String track = null, message = null;
        long offset = 0, max = 600000;
        boolean ok = false;
        if (syncSub < 0) {
            message = "Turn on a subtitle track in the player first.";
        } else {
            try {
                JSONObject t = api.subSyncTrack(syncItemId, syncSource, syncSub);
                if (t == null) {
                    message = "Install the JellySubSync plugin on the Jellyfin server.";
                } else {
                    ok = t.optBoolean("Supported");
                    track = t.optString("Label", null);
                    offset = t.optLong("OffsetMs");
                    max = t.optLong("MaxOffsetMs", max);
                    message = ok ? null : t.optString("Reason", "This track can't be shifted.");
                }
            } catch (Exception e) {
                message = e.getMessage();
            }
        }
        syncOpen = true;
        final String fTrack = track, fMessage = message;
        final long fOffset = offset, fMax = max;
        final boolean fOk = ok;
        main.post(new Runnable() {
            @Override public void run() {
                panel.hide();
                try {
                    syncBox.show(fTrack, fOffset, fMax, fOk, fMessage, new SyncBox.Listener() {
                        @Override public void apply(final long offsetMs) {
                            worker.post(new Runnable() {
                                @Override public void run() { applySync(offsetMs); }
                            });
                        }

                        @Override public void closed() {
                            syncOpen = false;
                        }
                    });
                } catch (Exception e) {
                    syncOpen = false;
                    setStatus("Could not draw the subtitle sync box: " + e.getMessage());
                }
            }
        });
    }

    /** Worker thread: shift on the server, then restart playback at the paused position on the synced track. */
    private void applySync(final long offsetMs) {
        if (!syncOpen || syncApplying) return;
        syncApplying = true;
        boxBusy(offsetMs == 0 ? "Back to the original subtitles..." : "Syncing subtitles...");
        try {
            Api api = Api.of(prefs);
            int idx = api.subSyncApply(syncItemId, syncSource, syncSub, offsetMs);
            boxBusy("Restarting playback...");
            api.playAt(syncSession, syncItemId, syncSource, syncTicks, syncAudio, idx);
            long now = System.currentTimeMillis();
            pendingSub = idx;
            pendingSince = now;
            pendingSent = false;
            castQuietUntil = now + 8000;
            resumedAt = 0;
            syncOpen = false;
            final String msg = "Subtitles " + SyncBox.format(offsetMs) + (offsetMs == 0 ? " (original)" : "");
            main.post(new Runnable() {
                @Override public void run() { syncBox.done(msg); }
            });
        } catch (final Exception e) {
            Log.w(TAG, "subtitle sync failed", e);
            final String msg = e instanceof Api.HttpError ? e.getMessage() : "Could not reach the server: " + e.getMessage();
            main.post(new Runnable() {
                @Override public void run() { syncBox.failed(msg); }
            });
        } finally {
            syncApplying = false;
        }
    }

    private void boxBusy(final String msg) {
        main.post(new Runnable() {
            @Override public void run() { syncBox.busy(msg); }
        });
    }

    private void toast(final String msg) {
        main.post(new Runnable() {
            @Override public void run() { Toast.makeText(WatchService.this, msg, Toast.LENGTH_LONG).show(); }
        });
    }

    /** Session lists this remote control command (top level in 10.9+, under Capabilities before). */
    private static boolean supports(JSONObject s, String command) {
        JSONArray a = s.optJSONArray("SupportedCommands");
        JSONObject caps = s.optJSONObject("Capabilities");
        if (a == null && caps != null) a = caps.optJSONArray("SupportedCommands");
        for (int i = 0; a != null && i < a.length(); i++) {
            if (command.equals(a.optString(i))) return true;
        }
        return false;
    }

    /** Index of the card up at t seconds, or -1. */
    private int cardAt(double t) {
        for (int i = 0; recapCards != null && i < recapCards.length(); i++) {
            JSONObject c = recapCards.optJSONObject(i);
            double at = c.optDouble("At"), secs = c.optDouble("Seconds");
            if (t >= at && t < at + secs) return i;
        }
        return -1;
    }

    private double recapEnd() {
        double end = 0;
        for (int i = 0; recapCards != null && i < recapCards.length(); i++) {
            JSONObject c = recapCards.optJSONObject(i);
            end = Math.max(end, c.optDouble("At") + c.optDouble("Seconds"));
        }
        return end;
    }

    private void showCard(final int idx) {
        if (idx == recapShown) return;
        recapShown = idx;
        final JSONObject c = idx < 0 ? null : recapCards.optJSONObject(idx);
        main.post(new Runnable() {
            @Override public void run() {
                if (c == null) {
                    recapCard.hide();
                    return;
                }
                try {
                    recapCard.show(c.optString("Title"), str(c, "Body"));
                } catch (Exception e) {
                    setStatus("Could not draw the recap card: " + e.getMessage());
                }
            }
        });
    }

    /** Pause mode over: hide the card and tell Jellycap whether to resume the episode. */
    private void finishPause(Api api, boolean resume) {
        String item = recapItem, device = recapDevice;
        clearRecap();
        try {
            api.recapRelease(item, device, resume);
        } catch (Exception e) {
            Log.w(TAG, "recap release failed", e);
        }
    }

    /** The playing item changed or stopped: cards for the old one are over. */
    private void endRecap(Api api) {
        if (recapCards != null && recapPause) {
            finishPause(api, false);
        } else {
            clearRecap();
        }
        recapItem = null;
        recapAskUntil = 0;
    }

    private void clearRecap() {
        recapCards = null;
        showCard(-1);
        recapShown = -1;
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
