package com.xjellys.tvoverlay;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/** Starts the overlay service after a reboot or an app update. */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        Prefs p = new Prefs(ctx);
        boolean boot = Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction());
        if (!p.signedIn() || !p.running() || (boot && !p.startOnBoot())) return;
        try {
            OverlayService.start(ctx);
        } catch (Exception e) {
            Log.w("JellyTvOverlay", "could not start on " + intent.getAction() + ": " + e.getClass().getSimpleName());
        }
    }
}
