package com.xjellys.tvoverlay.render;

import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.view.ViewTreeObserver;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.util.HashMap;
import java.util.Map;

import com.xjellys.tvoverlay.logic.FocusModel;
import com.xjellys.tvoverlay.logic.ValueState;
import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.model.Node;

/**
 * One full-screen TYPE_APPLICATION_OVERLAY window per shown layout. Passive layouts are not focusable or
 * touchable. Interactive layouts take the remote: keys are handled locally (focus ring, list scroll, text scroll,
 * value text, page flip) without rebuilding views or touching the network. show() on a showing window replaces
 * its content in place (same window, same stacking). Main thread only.
 */
public final class OverlayWindow {
    public interface Listener {
        void onAction(Layout l, String action, String elementId, JSONObject values);

        void onClosed(Layout l, String reason);

        void onPassthrough(String key);
    }

    private static final long TICK_MS = 100;

    private final Context ctx;
    private final ImageCache images;
    private final Listener listener;
    private final WindowManager wm;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Map<String, Runnable> debounce = new HashMap<String, Runnable>();

    private FrameLayout root;
    private Layout layout;
    private ValueState values;
    private Renderer.Refs refs;
    private FocusModel fm;
    private String focusedId;
    private boolean refocusPending;
    private boolean backArmed;
    private boolean keyFocus = true;
    private long shownAt;

    private final Runnable ttl = new Runnable() {
        @Override public void run() { finish("ttl"); }
    };

    private final Runnable tick = new Runnable() {
        @Override public void run() { onTick(); }
    };

    public OverlayWindow(Context c, ImageCache images, Listener l) {
        this.ctx = c;
        this.images = images;
        this.listener = l;
        this.wm = c.getSystemService(WindowManager.class);
    }

    public boolean interactive() {
        return layout != null && layout.interactive();
    }

    public boolean showing() {
        return root != null;
    }

    public Layout layout() {
        return layout;
    }

    public void show(Layout l) {
        stopTimers();
        DisplayMetrics dm = new DisplayMetrics();
        wm.getDefaultDisplay().getRealMetrics(dm);
        layout = l;
        values = new ValueState(l);
        refs = new Renderer.Refs();
        fm = null;
        focusedId = null;
        backArmed = false;
        boolean fresh = root == null;
        if (fresh) root = makeRoot(); else root.removeAllViews();
        if (l.dim > 0) {
            View dim = new View(ctx);
            dim.setBackgroundColor(Color.BLACK);
            dim.setAlpha(l.dim);
            root.addView(dim, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        }
        View content = l.root == null ? null : Renderer.build(ctx, l.root, dm.widthPixels, dm.heightPixels, images, values, refs);
        if (content != null) root.addView(content);
        refocusPending = true;
        if (fresh) {
            try {
                wm.addView(root, params());
            } catch (RuntimeException e) {
                root = null;
                layout = null;
                throw e;
            }
        } else {
            wm.updateViewLayout(root, params());
        }
        if (keyFocus && l.interactive()) root.requestFocus();
        shownAt = SystemClock.uptimeMillis();
        if (l.ttlMs > 0) main.postDelayed(ttl, l.ttlMs);
        if (!refs.pages.isEmpty() || !refs.sequences.isEmpty()) main.postDelayed(tick, TICK_MS);
    }

    /** Removes the window without reporting (the host closed it, or the caller reports itself). */
    public void close() {
        stopTimers();
        if (root != null) {
            try {
                wm.removeView(root);
            } catch (RuntimeException ignored) {
                // already gone
            }
        }
        root = null;
        layout = null;
    }

    /** For the window manager: only the newest interactive window holds the remote. */
    void setKeyFocus(boolean on) {
        if (on == keyFocus) return;
        keyFocus = on;
        if (root == null || layout == null || !layout.interactive()) return;
        wm.updateViewLayout(root, params());
        if (on) root.requestFocus();
    }

    private WindowManager.LayoutParams params() {
        int flags = WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN;
        if (layout.interactive()) {
            flags |= WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL;
            if (!keyFocus) flags |= WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE;
        } else {
            flags |= WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE;
        }
        WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY, flags, PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.TOP | Gravity.LEFT;
        lp.setTitle("JellyTvOverlay " + layout.id);
        return lp;
    }

    private FrameLayout makeRoot() {
        final FrameLayout r = new FrameLayout(ctx) {
            @Override
            public boolean dispatchKeyEvent(KeyEvent e) {
                return onKey(e) || super.dispatchKeyEvent(e);
            }
        };
        r.setFocusable(true);
        r.setFocusableInTouchMode(true);
        r.setLayoutDirection(View.LAYOUT_DIRECTION_LOCALE);
        r.getViewTreeObserver().addOnGlobalLayoutListener(new ViewTreeObserver.OnGlobalLayoutListener() {
            @Override public void onGlobalLayout() {
                if (refocusPending && r == root) {
                    refocusPending = false;
                    rebuildFocus();
                }
            }
        });
        return r;
    }

    private void stopTimers() {
        main.removeCallbacks(ttl);
        main.removeCallbacks(tick);
        for (Runnable d : debounce.values()) main.removeCallbacks(d);
        debounce.clear();
    }

    private void finish(String reason) {
        Layout l = layout;
        if (l == null) return;
        close();
        listener.onClosed(l, reason);
    }

    // ---- keys ----

    private boolean onKey(KeyEvent e) {
        if (layout == null || !layout.interactive()) return false;
        String k = Keys.name(e.getKeyCode());
        if (k == null) {
            // other media keys: passthrough layouts hand them to the player, plain interactive ones consume them
            String alias = Keys.mediaAlias(e.getKeyCode());
            if (alias == null) return false;
            if (e.getAction() == KeyEvent.ACTION_DOWN && layout.passthrough()) listener.onPassthrough(alias);
            return true;
        }
        if ("back".equals(k)) {
            // act on UP: closing on DOWN would hand the UP to the player underneath (it would leave playback)
            if (e.getAction() == KeyEvent.ACTION_DOWN && e.getRepeatCount() == 0) backArmed = true;
            if (e.getAction() == KeyEvent.ACTION_UP && backArmed && !e.isCanceled()) {
                backArmed = false;
                handle(k);
            }
            return true;
        }
        if (e.getAction() == KeyEvent.ACTION_DOWN) handle(k);
        return true;
    }

    private void handle(String k) {
        if (fm == null) rebuildFocus();
        FocusModel.Result r = fm.onKey(k);
        switch (r.kind) {
            case MOVED:
                applyFocus(r.elementId);
                break;
            case STEPPED:
                step(r.target, r.by);
                break;
            case ACTION:
                listener.onAction(layout, r.action, r.elementId, values.snapshot());
                break;
            case BACK:
                finish("back");
                break;
            case PASSTHROUGH:
                // FocusModel's decision stands: the player gets it (pages never steal seek keys)
                listener.onPassthrough(k);
                break;
            default:
                if (("up".equals(k) || "down".equals(k)) && scrollText(k)) break;
                flipPages(k);
                break;
        }
    }

    // ---- focus ----

    /** From the ScrollView extent: whether a scroll text can still move its content up (-1) or down (+1). */
    private final FocusModel.ScrollProbe scrollProbe = new FocusModel.ScrollProbe() {
        @Override public boolean canScroll(String id, String dir) {
            ScrollView sv = refs == null ? null : refs.scrollTexts.get(id);
            return sv != null && sv.canScrollVertically("down".equals(dir) ? 1 : -1);
        }
    };

    private void rebuildFocus() {
        Map<String, float[]> rects = new HashMap<String, float[]>();
        for (Map.Entry<String, View> e : refs.focusables.entrySet()) {
            View v = e.getValue();
            if (v.isShown() && v.getWidth() > 0) rects.put(e.getKey(), rectOf(v));
        }
        String keep = focusedId;
        fm = new FocusModel(layout, rects, scrollProbe);
        if (keep != null) fm.setFocused(keep);
        applyFocus(fm.focused());
    }

    /** Rect in window content coordinates, ignoring list scroll offsets, so it stays valid while lists scroll. */
    private float[] rectOf(View v) {
        float x = 0, y = 0;
        View cur = v;
        while (cur != null && cur != root) {
            x += cur.getLeft();
            y += cur.getTop();
            ViewParent p = cur.getParent();
            cur = p instanceof View ? (View) p : null;
        }
        return new float[] {x, y, x + v.getWidth(), y + v.getHeight()};
    }

    private void applyFocus(String id) {
        if (focusedId != null && !focusedId.equals(id)) {
            View old = refs.focusables.get(focusedId);
            if (old != null) old.setForeground(null);
        }
        focusedId = id;
        applyShowFor();
        if (id == null) return;
        View v = refs.focusables.get(id);
        if (v == null) return;
        v.setForeground(refs.rings.get(id));
        ensureVisible(id, v);
    }

    /** showFor nodes: visible only while their element has focus (local, no rebuild). */
    private void applyShowFor() {
        for (Map.Entry<View, Node> e : refs.showFor.entrySet()) {
            int vis = FocusModel.visibleFor(e.getValue(), focusedId) ? View.VISIBLE : View.GONE;
            if (e.getKey().getVisibility() != vis) e.getKey().setVisibility(vis);
        }
    }

    /** Scrolls the enclosing list just enough to show v (instant: no animation delay). */
    private void ensureVisible(String id, View v) {
        ViewGroup sc = refs.scrollerOf.get(id);
        if (sc == null) return;
        int x = 0, y = 0;
        View cur = v;
        while (cur != null && cur.getParent() != sc) {
            x += cur.getLeft();
            y += cur.getTop();
            ViewParent p = cur.getParent();
            cur = p instanceof View ? (View) p : null;
        }
        if (cur == null) return;
        x += cur.getLeft();
        y += cur.getTop();
        if (sc instanceof HorizontalScrollView) {
            int vis = sc.getWidth() - sc.getPaddingLeft() - sc.getPaddingRight();
            int sx = sc.getScrollX();
            if (x < sx) sc.scrollTo(x, 0);
            else if (x + v.getWidth() > sx + vis) sc.scrollTo(x + v.getWidth() - vis, 0);
        } else {
            int vis = sc.getHeight() - sc.getPaddingTop() - sc.getPaddingBottom();
            int sy = sc.getScrollY();
            if (y < sy) sc.scrollTo(0, y);
            else if (y + v.getHeight() > sy + vis) sc.scrollTo(0, y + v.getHeight() - vis);
        }
    }

    /** Up/down on a focused scroll text: moves its content by three lines, clamped. */
    private boolean scrollText(String k) {
        ScrollView sv = focusedId == null ? null : refs.scrollTexts.get(focusedId);
        if (sv == null || sv.getChildCount() == 0) return false;
        TextView tv = (TextView) sv.getChildAt(0);
        int visible = sv.getHeight() - sv.getPaddingTop() - sv.getPaddingBottom();
        int max = Math.max(0, tv.getHeight() - visible);
        int ny = sv.getScrollY() + ("down".equals(k) ? 1 : -1) * 3 * tv.getLineHeight();
        sv.scrollTo(0, Math.max(0, Math.min(max, ny)));
        return true;
    }

    // ---- values, pages, sequences ----

    private void step(final String target, double by) {
        if (!values.step(target, by)) return;
        TextView tv = refs.values.get(target);
        if (tv != null) tv.setText(values.display(target));
        final String action = refs.changedActions.get(target);
        if (action == null) return;
        Runnable old = debounce.get(target);
        if (old != null) main.removeCallbacks(old);
        Runnable fire = new Runnable() {
            @Override public void run() {
                debounce.remove(target);
                if (layout != null) listener.onAction(layout, action, target, values.snapshot());
            }
        };
        debounce.put(target, fire);
        main.postDelayed(fire, 400);
    }

    /** Left/right on a visible pages container with more than one page. */
    private boolean flipPages(String k) {
        boolean right = "right".equals(k);
        if (!right && !"left".equals(k)) return false;
        for (Renderer.Timed p : refs.pages) {
            if (p.kids.size() < 2 || !p.box.isShown()) continue;
            if (right) p.pager.next(); else p.pager.prev();
            p.show(p.pager.page());
            refocusPending = true;
            return true;
        }
        return false;
    }

    private void onTick() {
        if (root == null || layout == null) return;
        long t = SystemClock.uptimeMillis() - shownAt;
        for (Renderer.Timed p : refs.pages) {
            if (p.pager.tick(t)) {
                p.show(p.pager.page());
                refocusPending = true;
            }
        }
        for (Renderer.Timed s : refs.sequences) {
            int i = s.seq.index(t);
            if (i < 0) {
                Layout l = layout;
                String fa = s.node.finishedAction();
                if (fa != null) listener.onAction(l, fa, null, values.snapshot());
                if (layout == l) finish("finished"); // unless the listener already replaced or closed it
                return;
            }
            if (i != s.shown) {
                s.show(i);
                refocusPending = true;
            }
        }
        main.postDelayed(tick, TICK_MS);
    }
}
