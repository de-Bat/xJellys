package com.xjellys.jellyxr.tv;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/** The X-Ray panel drawn over the player. Main thread only. Never takes focus: the remote keeps driving the player. */
final class CastPanel {
    static final class Card {
        final String actor;
        final String character;
        final String info;
        final boolean onScreen;
        Bitmap face;

        Card(String actor, String character, String info, boolean onScreen) {
            this.actor = actor;
            this.character = character;
            this.info = info;
            this.onScreen = onScreen;
        }
    }

    private static final long PAGE_MS = 8000;
    private static final int BIG = 64, SMALL = 44, SECTION = 34, GAP = 10;

    private final Context ctx;
    private final WindowManager wm;
    private final Handler main = new Handler(Looper.getMainLooper());
    private FrameLayout root;
    /** Holds the rows. The window root gets its layout direction from the system, this one from the names. */
    private LinearLayout body;
    private List<List<Object>> pages = new ArrayList<>();
    private List<String> pageTitles = new ArrayList<>();
    private int page;

    private final Runnable nextPage = new Runnable() {
        @Override public void run() {
            if (root == null || pages.size() < 2) return;
            page = (page + 1) % pages.size();
            render();
            main.postDelayed(this, PAGE_MS);
        }
    };

    private final Runnable autoHide = new Runnable() {
        @Override public void run() { hide(); }
    };

    CastPanel(Context ctx) {
        this.ctx = ctx;
        this.wm = ctx.getSystemService(WindowManager.class);
    }

    boolean showing() {
        return root != null;
    }

    /** Shows the cards (on screen first). overlaySeconds 0 = until hide(). */
    void show(String header, List<Card> cards, int overlaySeconds) {
        hide();
        paginate(header, cards);
        if (pages.isEmpty()) return;

        root = new FrameLayout(ctx);
        root.setPadding(dp(18), dp(14), dp(18), dp(14));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Color.argb(220, 12, 14, 18));
        bg.setCornerRadius(dp(14));
        root.setBackground(bg);
        body = new LinearLayout(ctx);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setLayoutDirection(rtl ? View.LAYOUT_DIRECTION_RTL : View.LAYOUT_DIRECTION_LTR);
        root.addView(body, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT));

        WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
            dp(360), WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.TOP | Gravity.END;
        lp.x = dp(28);
        lp.y = dp(28);
        page = 0;
        render();
        try {
            wm.addView(root, lp);
        } catch (Exception e) {
            root = null;
            throw e;
        }
        root.setAlpha(0f);
        root.setTranslationX(dp(24));
        root.animate().alpha(1f).translationX(0f).setDuration(180).start();
        if (pages.size() > 1) main.postDelayed(nextPage, PAGE_MS);
        if (overlaySeconds > 0) main.postDelayed(autoHide, overlaySeconds * 1000L);
    }

    void hide() {
        main.removeCallbacks(nextPage);
        main.removeCallbacks(autoHide);
        if (root != null) {
            try {
                wm.removeView(root);
            } catch (Exception ignored) {
                // already gone
            }
        }
        root = null;
    }

    /** Splits cards into pages that fit the screen height. Entries are Card or String (section label). */
    private void paginate(String header, List<Card> cards) {
        pages = new ArrayList<>();
        pageTitles = new ArrayList<>();
        List<Card> on = new ArrayList<>(), rest = new ArrayList<>();
        for (Card c : cards) (c.onScreen ? on : rest).add(c);
        boolean split = !on.isEmpty() && !rest.isEmpty();

        float density = ctx.getResources().getDisplayMetrics().density;
        int screenDp = (int) (ctx.getResources().getDisplayMetrics().heightPixels / density);
        int budget = Math.max(BIG * 3, screenDp - 56 - 28 - 40);

        List<Object> cur = null;
        String curTitle = null;
        int used = 0;
        String section = null;
        List<Card> ordered = new ArrayList<>(on);
        ordered.addAll(rest);
        for (Card c : ordered) {
            String sec = c.onScreen ? "On screen" : (on.isEmpty() ? (header == null || header.isEmpty() ? "Cast" : header) : "Also in cast");
            int h = (split && !c.onScreen ? SMALL : BIG) + GAP;
            boolean newSection = !sec.equals(section);
            int need = h + (newSection && cur != null && !cur.isEmpty() ? SECTION : 0);
            if (cur == null || used + need > budget) {
                cur = new ArrayList<>();
                pages.add(cur);
                curTitle = sec;
                pageTitles.add(curTitle);
                used = 0;
                section = sec;
                newSection = false;
                need = h;
            }
            if (newSection) {
                cur.add(sec);
                section = sec;
            }
            cur.add(c);
            used += need;
        }
        // keep "split" for render: small rows only for the rest when someone is on screen
        smallRest = split;
        int r = 0;
        for (Card c : cards) if (isRtl(c.actor)) r++;
        rtl = r * 2 > cards.size();
    }

    private boolean smallRest;
    /** Right-to-left panel (face on the right) when most names are in Hebrew, Arabic, ... */
    private boolean rtl;

    private static boolean isRtl(String s) {
        if (s == null) return false;
        for (int i = 0; i < s.length(); ) {
            int cp = s.codePointAt(i);
            byte d = Character.getDirectionality(cp);
            if (d == Character.DIRECTIONALITY_RIGHT_TO_LEFT || d == Character.DIRECTIONALITY_RIGHT_TO_LEFT_ARABIC) return true;
            if (d == Character.DIRECTIONALITY_LEFT_TO_RIGHT) return false;
            i += Character.charCount(cp);
        }
        return false;
    }

    private void render() {
        if (root == null) return;
        body.removeAllViews();
        List<Object> entries = pages.get(page);

        LinearLayout head = new LinearLayout(ctx);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = label(pageTitles.get(page), 17, Color.WHITE, true);
        head.addView(title, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        if (pages.size() > 1) {
            head.addView(label((page + 1) + "/" + pages.size(), 13, Color.argb(160, 255, 255, 255), false));
        }
        body.addView(head);

        for (Object e : entries) {
            if (e instanceof String) {
                TextView sec = label(((String) e).toUpperCase(), 12, Color.argb(170, 255, 255, 255), false);
                sec.setLetterSpacing(0.06f);
                sec.setPadding(0, dp(10), 0, dp(2));
                View line = new View(ctx);
                line.setBackgroundColor(Color.argb(40, 255, 255, 255));
                LinearLayout.LayoutParams llp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1));
                llp.topMargin = dp(6);
                body.addView(line, llp);
                body.addView(sec);
                continue;
            }
            Card c = (Card) e;
            body.addView(row(c, smallRest && !c.onScreen));
        }
    }

    private View row(Card c, boolean small) {
        LinearLayout r = new LinearLayout(ctx);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setGravity(Gravity.CENTER_VERTICAL);
        int size = dp(small ? SMALL : BIG) - dp(4);

        FrameLayout face = new FrameLayout(ctx);
        GradientDrawable round = new GradientDrawable();
        round.setShape(GradientDrawable.OVAL);
        round.setColor(Color.rgb(58, 64, 78));
        face.setBackground(round);
        face.setClipToOutline(true);
        if (c.face != null) {
            ImageView img = new ImageView(ctx);
            img.setScaleType(ImageView.ScaleType.CENTER_CROP);
            img.setImageBitmap(c.face);
            face.addView(img, new FrameLayout.LayoutParams(size, size));
        } else {
            TextView ini = label(initials(c.actor), small ? 14 : 18, Color.argb(200, 255, 255, 255), true);
            ini.setGravity(Gravity.CENTER);
            face.addView(ini, new FrameLayout.LayoutParams(size, size));
        }
        LinearLayout.LayoutParams flp = new LinearLayout.LayoutParams(size, size);
        flp.setMarginEnd(dp(12));
        r.addView(face, flp);

        LinearLayout t = new LinearLayout(ctx);
        t.setOrientation(LinearLayout.VERTICAL);
        t.addView(label(c.actor, small ? 14 : 16, Color.WHITE, true));
        if (c.character != null && !c.character.isEmpty()) {
            t.addView(label(c.character, small ? 12 : 13, Color.argb(195, 255, 255, 255), false));
        }
        if (!small && c.info != null && !c.info.isEmpty()) {
            t.addView(label(c.info, 11, Color.argb(150, 255, 255, 255), false));
        }
        r.addView(t, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        rlp.topMargin = dp(GAP);
        r.setLayoutParams(rlp);
        return r;
    }

    private TextView label(String s, int sp, int color, boolean bold) {
        TextView v = new TextView(ctx);
        v.setText(s);
        v.setTextSize(sp);
        v.setTextColor(color);
        v.setSingleLine(true);
        v.setEllipsize(TextUtils.TruncateAt.END);
        // one paragraph direction for every line, so names and info lines align on the same side
        v.setTextDirection(rtl ? View.TEXT_DIRECTION_RTL : View.TEXT_DIRECTION_LTR);
        v.setTextAlignment(View.TEXT_ALIGNMENT_GRAVITY);
        v.setGravity((rtl ? Gravity.RIGHT : Gravity.LEFT) | Gravity.CENTER_VERTICAL);
        if (bold) v.setTypeface(Typeface.DEFAULT_BOLD);
        return v;
    }

    private static String initials(String name) {
        if (name == null) return "?";
        StringBuilder b = new StringBuilder();
        for (String p : name.trim().split("\\s+")) {
            if (!p.isEmpty() && b.length() < 2) b.appendCodePoint(p.codePointAt(0));
        }
        return b.length() == 0 ? "?" : b.toString().toUpperCase();
    }

    private int dp(int v) {
        return Math.round(v * ctx.getResources().getDisplayMetrics().density);
    }
}
