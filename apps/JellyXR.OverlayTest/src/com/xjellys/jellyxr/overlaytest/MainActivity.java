package com.xjellys.jellyxr.overlaytest;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/** Shows whether this box lets an app draw over other apps, and starts the overlay test. */
public class MainActivity extends Activity {
    private TextView status;
    private final Runnable refresher = new Runnable() {
        @Override public void run() { refresh(); }
    };

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        int pad = dp(32);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, pad, pad, pad);
        root.setBackgroundColor(Color.rgb(24, 28, 36));

        TextView title = text("JellyXR overlay test", 28);
        root.addView(title);

        status = text("", 18);
        status.setPadding(0, dp(16), 0, dp(16));
        root.addView(status);

        LinearLayout buttons = new LinearLayout(this);
        buttons.setOrientation(LinearLayout.HORIZONTAL);
        buttons.setGravity(Gravity.START);
        Button grant = button("1. Allow overlay", new View.OnClickListener() {
            @Override public void onClick(View v) { openOverlaySettings(); }
        });
        Button start = button("2. Start test", new View.OnClickListener() {
            @Override public void onClick(View v) {
                OverlayService.lastStatus = "Starting...";
                startForegroundService(new Intent(MainActivity.this, OverlayService.class));
                status.postDelayed(refresher, 800);
            }
        });
        Button stop = button("Stop", new View.OnClickListener() {
            @Override public void onClick(View v) {
                stopService(new Intent(MainActivity.this, OverlayService.class));
                status.postDelayed(refresher, 300);
            }
        });
        buttons.addView(grant);
        buttons.addView(start);
        buttons.addView(stop);
        root.addView(buttons);

        TextView help = text(
            "How to test:\n"
            + "1. Allow overlay (if the settings screen does not open, use the adb command above).\n"
            + "2. Start test: a box with a running clock appears top right.\n"
            + "3. Press Home, open Jellyfin, play a video and pause it.\n"
            + "   Pass = the box stays on top of the video, the clock keeps ticking,\n"
            + "   and the remote still controls Jellyfin.\n"
            + "4. Come back here and press Stop. The test also ends by itself after 10 minutes.", 16);
        help.setPadding(0, dp(24), 0, 0);
        root.addView(help);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(Color.rgb(24, 28, 36));
        scroll.addView(root);
        setContentView(scroll);
        start.requestFocus();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refresh();
    }

    private void refresh() {
        boolean allowed = Settings.canDrawOverlays(this);
        status.setText(
            "Device: " + Build.MANUFACTURER + " " + Build.MODEL + "\n"
            + "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")\n"
            + "Overlay permission: " + (allowed ? "ALLOWED" : "NOT ALLOWED") + "\n"
            + (allowed ? "" : "  adb shell appops set " + getPackageName() + " SYSTEM_ALERT_WINDOW allow\n")
            + "Test: " + OverlayService.lastStatus);
        status.setTextColor(allowed ? Color.rgb(140, 230, 160) : Color.rgb(255, 170, 120));
    }

    private void openOverlaySettings() {
        try {
            startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName())));
        } catch (ActivityNotFoundException e) {
            try {
                startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION));
            } catch (ActivityNotFoundException e2) {
                OverlayService.lastStatus = "This box has no overlay settings screen. Use the adb command.";
                refresh();
            }
        }
    }

    private TextView text(String s, int sp) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(Color.WHITE);
        return t;
    }

    private Button button(String label, View.OnClickListener click) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setOnClickListener(click);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMarginEnd(dp(12));
        b.setLayoutParams(lp);
        return b;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}
