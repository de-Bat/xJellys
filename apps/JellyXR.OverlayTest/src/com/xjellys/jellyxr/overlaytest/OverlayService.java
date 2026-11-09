package com.xjellys.jellyxr.overlaytest;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/** Foreground service that draws a small panel with a ticking clock over every app. */
public class OverlayService extends Service {
    static volatile String lastStatus = "not started";

    private static final String TAG = "JellyXROverlayTest";
    private static final String CHANNEL = "overlay-test";
    private static final long MAX_MS = 10 * 60 * 1000;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private WindowManager wm;
    private View panel;
    private TextView clock;
    private long startedAt;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            long up = System.currentTimeMillis() - startedAt;
            if (up > MAX_MS) {
                stopSelf();
                return;
            }
            if (clock != null) {
                clock.setText(new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date())
                    + "   running " + (up / 1000) + " s");
            }
            handler.postDelayed(this, 1000);
        }
    };

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Overlay test", NotificationManager.IMPORTANCE_LOW));
        Notification n = new Notification.Builder(this, CHANNEL)
            .setContentTitle("JellyXR overlay test running")
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .build();
        startForeground(1, n);

        if (panel != null) {
            return START_NOT_STICKY;
        }
        if (!Settings.canDrawOverlays(this)) {
            lastStatus = "FAILED: overlay permission not granted";
            stopSelf();
            return START_NOT_STICKY;
        }

        try {
            wm = getSystemService(WindowManager.class);
            panel = buildPanel();
            WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                // never take focus or input: the remote must keep controlling the app underneath
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                    | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT);
            lp.gravity = Gravity.TOP | Gravity.END;
            lp.x = dp(32);
            lp.y = dp(32);
            wm.addView(panel, lp);
            startedAt = System.currentTimeMillis();
            handler.post(tick);
            lastStatus = "RUNNING: overlay attached. Now open Jellyfin and check it stays on top.";
            Log.i(TAG, "overlay attached");
        } catch (Exception e) {
            lastStatus = "FAILED: " + e.getClass().getSimpleName() + ": " + e.getMessage();
            Log.e(TAG, "addView failed", e);
            panel = null;
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    private View buildPanel() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.HORIZONTAL);
        box.setGravity(Gravity.CENTER_VERTICAL);
        box.setPadding(dp(16), dp(12), dp(20), dp(12));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Color.argb(215, 12, 14, 18));
        bg.setCornerRadius(dp(12));
        box.setBackground(bg);

        // stand-in for an actor portrait: the app icon in a circle
        ImageView face = new ImageView(this);
        face.setImageResource(getApplicationInfo().icon);
        face.setScaleType(ImageView.ScaleType.CENTER_CROP);
        GradientDrawable round = new GradientDrawable();
        round.setShape(GradientDrawable.OVAL);
        round.setColor(Color.DKGRAY);
        face.setBackground(round);
        face.setClipToOutline(true);
        LinearLayout.LayoutParams flp = new LinearLayout.LayoutParams(dp(56), dp(56));
        flp.setMarginEnd(dp(14));
        box.addView(face, flp);

        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        TextView title = new TextView(this);
        title.setText("JellyXR overlay test");
        title.setTextColor(Color.WHITE);
        title.setTextSize(18);
        texts.addView(title);
        clock = new TextView(this);
        clock.setTextColor(Color.argb(200, 255, 255, 255));
        clock.setTextSize(14);
        texts.addView(clock);
        box.addView(texts);
        return box;
    }

    @Override
    public void onDestroy() {
        handler.removeCallbacks(tick);
        if (panel != null && wm != null) {
            try {
                wm.removeView(panel);
            } catch (Exception ignored) {
                // already gone
            }
        }
        panel = null;
        if (lastStatus.startsWith("RUNNING")) {
            long up = (System.currentTimeMillis() - startedAt) / 1000;
            lastStatus = "stopped after " + up + " s (if you did not press Stop and it was under 600 s, the box killed it)";
        }
        super.onDestroy();
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}
