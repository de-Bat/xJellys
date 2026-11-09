package com.xjellys.jellyxr.tv;

import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.Locale;

/**
 * Subtitle sync box over the player. Unlike the cast panel it takes focus, so while it is up the remote
 * drives the box, not the player: left / right = 0.25 s, up / down = 1 s, OK or play = apply, back = close.
 * Main thread only.
 */
final class SyncBox {
    interface Listener {
        /** OK / play pressed with this offset (ms, positive = subtitles later). */
        void apply(long offsetMs);

        /** Closed without applying (back, idle timeout, or hide()). */
        void closed();
    }

    static final long SMALL_STEP = 250, BIG_STEP = 1000;
    private static final long IDLE_MS = 15000;

    private final Context ctx;
    private final WindowManager wm;
    private final Handler main = new Handler(Looper.getMainLooper());
    private FrameLayout root;
    private TextView offsetView, statusView, hintView;
    private Listener listener;
    private long offset, max;
    private boolean supported, busy;

    private final Runnable idle = new Runnable() {
        @Override public void run() { close(); }
    };

    SyncBox(Context ctx) {
        this.ctx = ctx;
        this.wm = ctx.getSystemService(WindowManager.class);
    }

    boolean showing() {
        return root != null;
    }

    /**
     * Shows the box. supported false = only the message (why the track can't be shifted) and back.
     * maxMs bounds the offset either way.
     */
    void show(String track, long offsetMs, long maxMs, boolean supported, String message, Listener l) {
        hide();
        this.listener = l;
        this.offset = offsetMs;
        this.max = Math.max(BIG_STEP, maxMs);
        this.supported = supported;
        this.busy = false;

        root = new FrameLayout(ctx) {
            @Override
            public boolean dispatchKeyEvent(KeyEvent e) {
                return onKey(e) || super.dispatchKeyEvent(e);
            }
        };
        root.setFocusable(true);
        root.setFocusableInTouchMode(true);
        int pad = dp(18);
        root.setPadding(pad, dp(14), pad, dp(14));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Color.argb(225, 16, 18, 24));
        bg.setCornerRadius(dp(14));
        bg.setStroke(dp(1), Color.argb(60, 255, 255, 255));
        root.setBackground(bg);

        LinearLayout body = new LinearLayout(ctx);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setGravity(Gravity.CENTER_HORIZONTAL);
        root.addView(body, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT));

        body.addView(label("Subtitle sync", 17, Color.WHITE, true));
        if (track != null && !track.isEmpty()) body.addView(label(track, 13, Color.argb(170, 255, 255, 255), false));

        if (supported) {
            LinearLayout row = new LinearLayout(ctx);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER);
            row.setPadding(0, dp(10), 0, dp(4));
            row.addView(label("◀ earlier", 13, Color.argb(150, 255, 255, 255), false));
            offsetView = label("", 30, Color.WHITE, true);
            LinearLayout.LayoutParams olp = new LinearLayout.LayoutParams(dp(150), LinearLayout.LayoutParams.WRAP_CONTENT);
            row.addView(offsetView, olp);
            row.addView(label("later ▶", 13, Color.argb(150, 255, 255, 255), false));
            body.addView(row);
            hintView = label("▲▼ 1 s   ◀▶ 0.25 s   OK apply and play   BACK close", 12, Color.argb(140, 255, 255, 255), false);
            body.addView(hintView);
        }
        statusView = label(message == null ? "" : message, 13, supported ? Color.rgb(170, 235, 185) : Color.rgb(255, 190, 140), false);
        statusView.setPadding(0, dp(8), 0, 0);
        statusView.setSingleLine(false);
        statusView.setMaxLines(3);
        statusView.setVisibility(message == null || message.isEmpty() ? View.GONE : View.VISIBLE);
        body.addView(statusView);
        if (!supported) body.addView(label("BACK close", 12, Color.argb(140, 255, 255, 255), false));
        render();

        WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
            dp(420), WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            // focusable (no FLAG_NOT_FOCUSABLE): the box gets the remote while it is up
            WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        lp.y = dp(48);
        try {
            wm.addView(root, lp);
        } catch (RuntimeException e) {
            root = null;
            throw e;
        }
        root.requestFocus();
        root.setAlpha(0f);
        root.animate().alpha(1f).setDuration(150).start();
        bumpIdle();
    }

    /** Busy line while the server works ("Syncing..."); keys other than back are ignored until done. */
    void busy(String message) {
        busy = true;
        status(message, Color.rgb(200, 210, 255));
        main.removeCallbacks(idle);
    }

    /** Applying failed: show why and let the viewer adjust again. */
    void failed(String message) {
        busy = false;
        status(message, Color.rgb(255, 190, 140));
        bumpIdle();
    }

    /** Applied: show the result shortly, then go away. */
    void done(String message) {
        busy = true;
        status(message, Color.rgb(170, 235, 185));
        main.removeCallbacks(idle);
        main.postDelayed(new Runnable() {
            @Override public void run() { hide(); }
        }, 1500);
    }

    /** Remove the box. Does not call the listener. */
    void hide() {
        main.removeCallbacks(idle);
        if (root != null) {
            try {
                wm.removeView(root);
            } catch (Exception ignored) {
                // already gone
            }
        }
        root = null;
        listener = null;
    }

    private void close() {
        Listener l = listener;
        hide();
        if (l != null) l.closed();
    }

    private boolean onKey(KeyEvent e) {
        int k = e.getKeyCode();
        boolean down = e.getAction() == KeyEvent.ACTION_DOWN;
        if (k == KeyEvent.KEYCODE_BACK || k == KeyEvent.KEYCODE_ESCAPE || k == KeyEvent.KEYCODE_MEDIA_STOP) {
            if (!down && e.getRepeatCount() == 0) close();
            return true;
        }
        if (!down) return isOurs(k);
        if (busy || !supported) return isOurs(k);
        bumpIdle();
        switch (k) {
            case KeyEvent.KEYCODE_DPAD_LEFT: step(-SMALL_STEP); return true;
            case KeyEvent.KEYCODE_DPAD_RIGHT: step(SMALL_STEP); return true;
            case KeyEvent.KEYCODE_DPAD_DOWN: step(-BIG_STEP); return true;
            case KeyEvent.KEYCODE_DPAD_UP: step(BIG_STEP); return true;
            case KeyEvent.KEYCODE_MEDIA_REWIND: step(-BIG_STEP); return true;
            case KeyEvent.KEYCODE_MEDIA_FAST_FORWARD: step(BIG_STEP); return true;
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
            case KeyEvent.KEYCODE_MEDIA_PLAY:
            case KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE:
                if (e.getRepeatCount() == 0 && listener != null) listener.apply(offset);
                return true;
            default:
                return false;
        }
    }

    private static boolean isOurs(int k) {
        switch (k) {
            case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_RIGHT:
            case KeyEvent.KEYCODE_DPAD_UP:
            case KeyEvent.KEYCODE_DPAD_DOWN:
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
            case KeyEvent.KEYCODE_MEDIA_PLAY:
            case KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE:
            case KeyEvent.KEYCODE_MEDIA_REWIND:
            case KeyEvent.KEYCODE_MEDIA_FAST_FORWARD:
                return true;
            default:
                return false;
        }
    }

    private void step(long ms) {
        offset = Math.max(-max, Math.min(max, offset + ms));
        if (statusView != null) statusView.setVisibility(View.GONE);
        render();
    }

    private void render() {
        if (offsetView == null) return;
        offsetView.setText(format(offset));
        offsetView.setTextColor(offset == 0 ? Color.argb(200, 255, 255, 255) : Color.WHITE);
    }

    /** +1.25 s, -0.5 s, 0 s (original). */
    static String format(long ms) {
        if (ms == 0) return "0 s";
        String s = String.format(Locale.US, "%.2f", Math.abs(ms) / 1000.0);
        if (s.endsWith("0")) s = s.substring(0, s.length() - 1);
        if (s.endsWith(".0")) s = s.substring(0, s.length() - 2);
        return (ms > 0 ? "+" : "−") + s + " s";
    }

    private void status(String message, int color) {
        if (statusView == null) return;
        statusView.setText(message);
        statusView.setTextColor(color);
        statusView.setVisibility(View.VISIBLE);
    }

    private void bumpIdle() {
        main.removeCallbacks(idle);
        main.postDelayed(idle, IDLE_MS);
    }

    private TextView label(String s, int sp, int color, boolean bold) {
        TextView v = new TextView(ctx);
        v.setText(s);
        v.setTextSize(sp);
        v.setTextColor(color);
        v.setGravity(Gravity.CENTER);
        v.setSingleLine(true);
        if (bold) v.setTypeface(Typeface.DEFAULT_BOLD);
        return v;
    }

    private int dp(int v) {
        return Math.round(v * ctx.getResources().getDisplayMetrics().density);
    }
}
