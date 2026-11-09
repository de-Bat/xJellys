package com.xjellys.jellyxr.tv;

import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * One Jellycap recap text card (episode title + summary) drawn over the lower part of the player.
 * Main thread only. Never takes focus: the remote keeps driving the player.
 */
final class RecapCard {
    private final Context ctx;
    private final WindowManager wm;
    private LinearLayout root;

    RecapCard(Context ctx) {
        this.ctx = ctx;
        this.wm = ctx.getSystemService(WindowManager.class);
    }

    void show(String title, String body) {
        boolean rtl = isRtl(title + " " + (body == null ? "" : body));
        if (root == null) {
            root = new LinearLayout(ctx);
            root.setOrientation(LinearLayout.VERTICAL);
            root.setPadding(dp(28), dp(20), dp(28), dp(22));
            GradientDrawable bg = new GradientDrawable();
            bg.setColor(Color.argb(225, 12, 14, 18));
            bg.setCornerRadius(dp(16));
            root.setBackground(bg);

            int width = Math.min(dp(900), Math.round(ctx.getResources().getDisplayMetrics().widthPixels * 0.72f));
            WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
                width, WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                    | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT);
            lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            lp.y = dp(48);
            fill(title, body, rtl);
            try {
                wm.addView(root, lp);
            } catch (Exception e) {
                root = null;
                throw e;
            }
            root.setAlpha(0f);
            root.animate().alpha(1f).setDuration(250).start();
        } else {
            fill(title, body, rtl);
        }
    }

    void hide() {
        if (root != null) {
            try {
                wm.removeView(root);
            } catch (Exception ignored) {
                // already gone
            }
        }
        root = null;
    }

    private void fill(String title, String body, boolean rtl) {
        root.removeAllViews();
        root.setLayoutDirection(rtl ? View.LAYOUT_DIRECTION_RTL : View.LAYOUT_DIRECTION_LTR);
        root.addView(label(title, 22, Color.WHITE, true, rtl));
        if (body != null && !body.isEmpty()) {
            TextView b = label(body, 17, Color.argb(220, 255, 255, 255), false, rtl);
            b.setLineSpacing(0, 1.15f);
            b.setPadding(0, dp(10), 0, 0);
            root.addView(b);
        }
    }

    private TextView label(String s, int sp, int color, boolean bold, boolean rtl) {
        TextView v = new TextView(ctx);
        v.setText(s);
        v.setTextSize(sp);
        v.setTextColor(color);
        v.setTextDirection(rtl ? View.TEXT_DIRECTION_RTL : View.TEXT_DIRECTION_LTR);
        v.setTextAlignment(View.TEXT_ALIGNMENT_GRAVITY);
        v.setGravity(rtl ? Gravity.RIGHT : Gravity.LEFT);
        if (bold) v.setTypeface(Typeface.DEFAULT_BOLD);
        return v;
    }

    /** Right to left when most letters are Hebrew, Arabic, ... (titles start with a Latin "S01E02"). */
    private static boolean isRtl(String s) {
        int rtl = 0, ltr = 0;
        for (int i = 0; i < s.length(); ) {
            int cp = s.codePointAt(i);
            byte d = Character.getDirectionality(cp);
            if (d == Character.DIRECTIONALITY_RIGHT_TO_LEFT || d == Character.DIRECTIONALITY_RIGHT_TO_LEFT_ARABIC) rtl++;
            else if (d == Character.DIRECTIONALITY_LEFT_TO_RIGHT) ltr++;
            i += Character.charCount(cp);
        }
        return rtl > ltr;
    }

    private int dp(int v) {
        return Math.round(v * ctx.getResources().getDisplayMetrics().density);
    }
}
