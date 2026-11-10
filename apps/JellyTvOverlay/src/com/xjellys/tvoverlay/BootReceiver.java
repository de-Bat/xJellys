package com.xjellys.tvoverlay;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Starts the overlay service after a reboot or an app update. The service itself arrives in a later task. */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        // intentionally empty until the overlay service exists
    }
}
