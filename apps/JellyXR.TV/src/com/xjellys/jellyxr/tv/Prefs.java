package com.xjellys.jellyxr.tv;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.provider.Settings;

import java.util.UUID;

/** Saved sign-in and options. */
final class Prefs {
    private final SharedPreferences sp;
    private final Context ctx;

    Prefs(Context ctx) {
        this.ctx = ctx.getApplicationContext();
        this.sp = this.ctx.getSharedPreferences("jellyxr", Context.MODE_PRIVATE);
    }

    String server() { return sp.getString("server", ""); }
    String token() { return sp.getString("token", ""); }
    String userName() { return sp.getString("userName", ""); }

    /** Device id of the playback session to follow; empty = automatic. */
    String watch() { return sp.getString("watch", ""); }
    String watchLabel() { return sp.getString("watchLabel", ""); }

    boolean startOnBoot() { return sp.getBoolean("boot", true); }

    /** False after the user pressed Stop: the service stays off, also after a reboot. */
    boolean running() { return sp.getBoolean("running", true); }

    boolean signedIn() { return !server().isEmpty() && !token().isEmpty(); }

    void setServer(String s) { sp.edit().putString("server", s).apply(); }

    void setSession(String token, String userName) {
        sp.edit().putString("token", token).putString("userName", userName).apply();
    }

    void signOut() { sp.edit().remove("token").remove("userName").apply(); }

    void setWatch(String deviceId, String label) {
        sp.edit().putString("watch", deviceId).putString("watchLabel", label).apply();
    }

    void setStartOnBoot(boolean on) { sp.edit().putBoolean("boot", on).apply(); }

    void setRunning(boolean on) { sp.edit().putBoolean("running", on).apply(); }

    /** Stable random id for this app install (Jellyfin keys sessions and tokens by it). */
    String deviceId() {
        String id = sp.getString("deviceId", "");
        if (id.isEmpty()) {
            id = "jellyxr-tv-" + UUID.randomUUID();
            sp.edit().putString("deviceId", id).apply();
        }
        return id;
    }

    /** The box's name as set in Android settings; Jellyfin for Android TV reports the same name. */
    String deviceName() {
        String n = null;
        try {
            n = Settings.Global.getString(ctx.getContentResolver(), Settings.Global.DEVICE_NAME);
        } catch (Exception ignored) {
            // older boxes
        }
        if (n == null || n.trim().isEmpty()) n = Build.MODEL;
        return n;
    }
}
