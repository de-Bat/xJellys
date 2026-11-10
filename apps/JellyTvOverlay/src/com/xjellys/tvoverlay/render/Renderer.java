package com.xjellys.tvoverlay.render;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.text.TextDirectionHeuristics;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import com.xjellys.tvoverlay.logic.Pager;
import com.xjellys.tvoverlay.logic.Sequencer;
import com.xjellys.tvoverlay.logic.ValueState;
import com.xjellys.tvoverlay.model.Node;

/**
 * Builds Android views for a layout node tree, once per show. Sizes: x y w h are fractions of the parent's inner
 * box; size, pad, gap and radius are fractions of the screen height. A missing size fills the cross axis (and the
 * whole box in a stack) and wraps on the main axis of a row/column; text, value, button and icon wrap. Unknown node
 * types give no view. Everything key handling needs later (focus targets, rings, list scrollers, value views,
 * scroll texts, pages and sequences) is collected in {@link Refs} so a key press never rebuilds. Nodes with
 * {@code showFor} start hidden; the window shows them while their element has focus.
 */
public final class Renderer {
    static final int RING = 0xFF4AA3FF;
    static final int RING_FILL = 0x224AA3FF;
    private static final int MATCH = ViewGroup.LayoutParams.MATCH_PARENT;
    private static final int WRAP = ViewGroup.LayoutParams.WRAP_CONTENT;

    private enum Parent { STACK, ROW, COLUMN }

    /** A pages or sequence container: only one child visible at a time. */
    static final class Timed {
        final Node node;
        final List<View> kids = new ArrayList<View>();
        final List<Integer> durations = new ArrayList<Integer>();
        Pager pager;
        Sequencer seq;
        View box;
        int shown = -1;

        Timed(Node n) {
            node = n;
        }

        void show(int i) {
            if (i == shown || i < 0 || i >= kids.size()) return;
            if (shown >= 0) kids.get(shown).setVisibility(View.GONE);
            kids.get(i).setVisibility(View.VISIBLE);
            shown = i;
        }
    }

    static final class Refs {
        final Map<String, View> focusables = new LinkedHashMap<String, View>();
        final Map<String, Drawable> rings = new HashMap<String, Drawable>();
        /** focusable id to the nearest enclosing list scroller. */
        final Map<String, ViewGroup> scrollerOf = new HashMap<String, ViewGroup>();
        final Map<String, TextView> values = new HashMap<String, TextView>();
        final Map<String, String> changedActions = new HashMap<String, String>();
        /** scroll:true text id to the ScrollView wrapping its TextView. */
        final Map<String, ScrollView> scrollTexts = new HashMap<String, ScrollView>();
        final List<Timed> pages = new ArrayList<Timed>();
        final List<Timed> sequences = new ArrayList<Timed>();
        /** Views of showFor nodes, with their node (visibility follows focus). */
        final Map<View, Node> showFor = new LinkedHashMap<View, Node>();
    }

    private final Context ctx;
    private final int sh;
    private final ImageCache images;
    private final ValueState values;
    private final Refs refs;
    private final float density;

    private Renderer(Context c, int sh, ImageCache images, ValueState values, Refs refs) {
        this.ctx = c;
        this.sh = sh;
        this.images = images;
        this.values = values;
        this.refs = refs;
        this.density = c.getResources().getDisplayMetrics().density;
    }

    public static View build(Context c, Node n, int screenW, int screenH, ImageCache images, ValueState values) {
        return build(c, n, screenW, screenH, images, values, new Refs());
    }

    /** The returned view carries FrameLayout params for a full-screen parent; null for an unknown root type. */
    static View build(Context c, Node n, int screenW, int screenH, ImageCache images, ValueState values, Refs refs) {
        return new Renderer(c, screenH, images, values, refs).node(n, Parent.STACK, screenW, screenH, null);
    }

    private int px(double v) {
        return (int) Math.round(v);
    }

    private View node(Node n, Parent parent, int pw, int ph, ViewGroup scroller) {
        String t = n.type;
        int w = Float.isNaN(n.w) ? -1 : px(n.w * pw);
        int h = Float.isNaN(n.h) ? -1 : px(n.h * ph);
        boolean round = "image".equals(t) && n.round();
        if (round && (w < 0) != (h < 0)) {
            if (w < 0) w = h; else h = w; // round with one size given: a circle
        }
        if ("icon".equals(t) && w < 0 && h < 0) w = h = px(n.size() * sh * 1.3);
        int lw = w >= 0 ? w : defaultSize(t, parent, true);
        int lh = h >= 0 ? h : defaultSize(t, parent, false);
        int pad = px(n.pad * sh);
        int ibw = Math.max(0, (lw > 0 ? lw : pw) - 2 * pad);
        int ibh = Math.max(0, (lh > 0 ? lh : ph) - 2 * pad);

        View v;
        if ("stack".equals(t)) {
            FrameLayout f = new FrameLayout(ctx);
            for (Node c : n.children) add(f, node(c, Parent.STACK, ibw, ibh, scroller), null, 0);
            v = f;
        } else if ("row".equals(t) || "column".equals(t)) {
            boolean row = "row".equals(t);
            v = linear(n, row, ibw, ibh, scroller);
        } else if ("list".equals(t)) {
            boolean horiz = "horizontal".equals(n.orientation());
            ViewGroup sc = horiz ? new HorizontalScrollView(ctx) : new ScrollView(ctx);
            sc.setFocusable(false);
            sc.setHorizontalScrollBarEnabled(false);
            sc.setVerticalScrollBarEnabled(false);
            sc.setClipToPadding(false);
            LinearLayout inner = linear(n, horiz, ibw, ibh, sc);
            inner.setPadding(0, 0, 0, 0);
            sc.addView(inner, new FrameLayout.LayoutParams(horiz ? WRAP : MATCH, horiz ? MATCH : WRAP));
            v = sc;
        } else if ("pages".equals(t) || "sequence".equals(t)) {
            v = timed(n, "pages".equals(t), ibw, ibh, scroller);
        } else if ("text".equals(t)) {
            v = text(n);
            if (v instanceof ScrollView && h < 0 && n.maxLines() > 0) {
                // a scroll text shows maxLines lines; the rest scrolls inside the box
                lh = n.maxLines() * ((TextView) ((ScrollView) v).getChildAt(0)).getLineHeight() + 2 * pad;
            }
        } else if ("value".equals(t)) {
            v = value(n);
        } else if ("image".equals(t)) {
            ImageView iv = new ImageView(ctx);
            iv.setScaleType("contain".equals(n.fit()) ? ImageView.ScaleType.FIT_CENTER : ImageView.ScaleType.CENTER_CROP);
            v = iv;
        } else if ("icon".equals(t)) {
            v = icon(n.icon(), n.color());
        } else if ("button".equals(t)) {
            v = button(n);
        } else {
            return null;
        }

        if (!"button".equals(t)) v.setPadding(pad, pad, pad, pad);
        if (!"button".equals(t)) background(v, n, round, "image".equals(t) ? Integer.valueOf(0x33FFFFFF) : null, 0);
        v.setLayoutParams(params(n, parent, lw, lh, pw, ph, round));
        if (n.showFor() != null) {
            v.setVisibility(View.GONE);
            refs.showFor.put(v, n);
        }
        if (n.focusable && n.id != null) {
            refs.focusables.put(n.id, v);
            refs.rings.put(n.id, ring(round, "button".equals(t) && n.radius <= 0 ? 0.01f * sh : n.radius * sh));
            if (scroller != null) refs.scrollerOf.put(n.id, scroller);
        }
        if (v instanceof ImageView && "image".equals(t) && images != null) images.into((ImageView) v, n.src());
        return v;
    }

    /** Size for a missing w (width=true) or h. */
    private static int defaultSize(String t, Parent p, boolean width) {
        if ("button".equals(t) || "icon".equals(t) || "value".equals(t)) return WRAP;
        if ("text".equals(t)) return width && p != Parent.ROW ? MATCH : WRAP;
        if (p == Parent.STACK) return MATCH;
        boolean main = (p == Parent.ROW) == width;
        return main ? WRAP : MATCH;
    }

    /** round = a circle image: centred on the cross axis of a row/column. */
    private ViewGroup.LayoutParams params(Node n, Parent p, int lw, int lh, int pw, int ph, boolean round) {
        if (p == Parent.STACK) {
            FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(lw, lh);
            lp.gravity = Gravity.TOP | Gravity.LEFT;
            lp.leftMargin = Float.isNaN(n.x) ? 0 : px(n.x * pw);
            lp.topMargin = Float.isNaN(n.y) ? 0 : px(n.y * ph);
            return lp;
        }
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(lw, lh);
        if (round) {
            lp.gravity = p == Parent.ROW ? Gravity.CENTER_VERTICAL : Gravity.CENTER_HORIZONTAL;
        }
        return lp;
    }

    private void add(ViewGroup g, View child, Parent p, int gap) {
        if (child == null) return;
        if (gap > 0 && g.getChildCount() > 0 && child.getLayoutParams() instanceof LinearLayout.LayoutParams) {
            LinearLayout.LayoutParams lp = (LinearLayout.LayoutParams) child.getLayoutParams();
            if (p == Parent.ROW) lp.setMarginStart(gap); else lp.topMargin = gap;
        }
        g.addView(child);
    }

    private LinearLayout linear(Node n, boolean row, int ibw, int ibh, ViewGroup scroller) {
        LinearLayout l = new LinearLayout(ctx);
        l.setOrientation(row ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        Parent p = row ? Parent.ROW : Parent.COLUMN;
        int gap = px(n.gap * sh);
        for (Node c : n.children) add(l, node(c, p, ibw, ibh, scroller), p, gap);
        return l;
    }

    private View timed(Node n, boolean pages, int ibw, int ibh, ViewGroup scroller) {
        FrameLayout f = new FrameLayout(ctx);
        Timed tm = new Timed(n);
        tm.box = f;
        for (Node c : n.children) {
            View cv = node(c, Parent.STACK, ibw, ibh, scroller);
            if (cv == null) continue;
            cv.setVisibility(View.GONE);
            f.addView(cv);
            tm.kids.add(cv);
            tm.durations.add(Integer.valueOf(c.durationMs()));
        }
        if (pages) {
            tm.pager = new Pager(tm.kids.size(), n.intervalMs());
            refs.pages.add(tm);
        } else {
            int[] d = new int[tm.durations.size()];
            for (int i = 0; i < d.length; i++) d[i] = tm.durations.get(i).intValue();
            tm.seq = new Sequencer(d);
            refs.sequences.add(tm);
        }
        tm.show(0);
        return f;
    }

    private TextView label(String s, Node n, Integer color) {
        TextView tv = new TextView(ctx);
        tv.setText(s);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_PX, n.size() * sh);
        tv.setTextColor(color != null ? color.intValue() : Color.WHITE);
        if (n.bold()) tv.setTypeface(Typeface.DEFAULT_BOLD);
        String d = "text".equals(n.type) || "button".equals(n.type) ? n.dir() : "ltr";
        boolean rtl = "rtl".equals(d)
            || ("auto".equals(d) && TextDirectionHeuristics.FIRSTSTRONG_LTR.isRtl(s, 0, s.length()));
        // paragraph direction from this node's own content, aligned to its start side (as CastPanel does)
        tv.setTextDirection(rtl ? View.TEXT_DIRECTION_RTL : View.TEXT_DIRECTION_LTR);
        tv.setTextAlignment(View.TEXT_ALIGNMENT_GRAVITY);
        tv.setGravity((rtl ? Gravity.RIGHT : Gravity.LEFT) | Gravity.CENTER_VERTICAL);
        return tv;
    }

    /** Plain text: maxLines with an ellipsis. scroll:true: the full text in a ScrollView (not focusable). */
    private View text(Node n) {
        TextView tv = label(n.text(), n, n.color());
        int ml = n.maxLines();
        if (!n.scroll()) {
            if (ml > 0) {
                tv.setMaxLines(ml);
                tv.setEllipsize(TextUtils.TruncateAt.END);
            }
            return tv;
        }
        ScrollView sv = new ScrollView(ctx);
        sv.setFocusable(false);
        sv.setVerticalScrollBarEnabled(false);
        sv.setOverScrollMode(View.OVER_SCROLL_NEVER);
        sv.addView(tv, new FrameLayout.LayoutParams(MATCH, WRAP));
        if (n.id != null) refs.scrollTexts.put(n.id, sv);
        return sv;
    }

    private View value(Node n) {
        TextView tv = label(values == null ? "" : values.display(n.id), n, n.color());
        if (n.id != null) {
            refs.values.put(n.id, tv);
            if (n.changedAction() != null) refs.changedActions.put(n.id, n.changedAction());
        }
        return tv;
    }

    private ImageView icon(String name, Integer color) {
        ImageView iv = new ImageView(ctx);
        int res = Icons.res(name);
        if (res != 0) iv.setImageResource(res);
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        if (color != null) iv.setImageTintList(ColorStateList.valueOf(color.intValue()));
        return iv;
    }

    private View button(Node n) {
        LinearLayout b = new LinearLayout(ctx);
        b.setOrientation(LinearLayout.HORIZONTAL);
        b.setGravity(Gravity.CENTER);
        int ts = px(n.size() * sh);
        int ph = n.pad > 0 ? px(n.pad * sh) : px(0.02 * sh);
        int pv = n.pad > 0 ? px(n.pad * sh) : px(0.01 * sh);
        b.setPadding(ph, pv, ph, pv);
        background(b, n, false, Integer.valueOf(0x33FFFFFF), n.radius > 0 ? 0 : 0.01f * sh);
        if (Icons.res(n.icon()) != 0) {
            ImageView iv = icon(n.icon(), n.color());
            int s = px(ts * 1.2);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(s, s);
            if (!n.label().isEmpty()) lp.setMarginEnd(px(ts * 0.4));
            b.addView(iv, lp);
        }
        if (!n.label().isEmpty()) b.addView(label(n.label(), n, n.color()));
        if (!n.enabled) b.setAlpha(0.4f);
        return b;
    }

    /** bg colour (or fallback) with the node radius (or defRadius px); clips children to the shape. */
    private void background(View v, Node n, boolean oval, Integer fallback, float defRadius) {
        Integer col = n.bg != null ? n.bg : fallback;
        float r = n.radius > 0 ? n.radius * sh : defRadius;
        if (col == null && r <= 0 && !oval) return;
        GradientDrawable g = new GradientDrawable();
        if (oval) g.setShape(GradientDrawable.OVAL); else g.setCornerRadius(r);
        g.setColor(col == null ? 0 : col.intValue());
        v.setBackground(g);
        if (oval || r > 0) v.setClipToOutline(true);
    }

    /** Focus look from the mockup: 2 dp #4AA3FF outline over a #224AA3FF fill. */
    private Drawable ring(boolean oval, float radius) {
        GradientDrawable g = new GradientDrawable();
        if (oval) g.setShape(GradientDrawable.OVAL); else g.setCornerRadius(Math.max(0, radius));
        g.setColor(RING_FILL);
        g.setStroke(Math.max(1, Math.round(2 * density)), RING);
        return g;
    }
}
