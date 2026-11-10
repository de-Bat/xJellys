package com.xjellys.tvoverlay.render;

import android.content.Context;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.model.Node;

/**
 * The shown overlay windows by layout id. Several interactive windows may coexist (a child over its parent);
 * the remote goes to the newest one, the others stay visible but unfocusable, and when the top one closes the
 * next interactive window still shown gets the remote back. Showing an id that is already shown replaces it in
 * place (same window and stacking, same place in the focus order). Main thread only.
 */
public final class OverlayManager {
    private final Context ctx;
    private final ImageCache images;
    private final OverlayWindow.Listener out;
    private final Map<String, OverlayWindow> windows = new LinkedHashMap<String, OverlayWindow>();
    /** Interactive layout ids, oldest first; the last one holds the remote. */
    private final List<String> stack = new ArrayList<String>();

    public OverlayManager(Context c, ImageCache images, OverlayWindow.Listener l) {
        this.ctx = c;
        this.images = images;
        this.out = l;
    }

    public void show(Layout l) {
        OverlayWindow w = windows.get(l.id);
        if (w == null) {
            w = new OverlayWindow(ctx, images, relay(l.id));
            windows.put(l.id, w);
        }
        if (l.interactive()) {
            if (!stack.contains(l.id)) stack.add(l.id);
        } else {
            stack.remove(l.id);
        }
        // before adding: only the newest interactive id keeps key focus, so a new window is the only focusable one
        refocus();
        try {
            w.show(l);
        } catch (RuntimeException e) {
            // no overlay permission or the window was refused: forget it
            windows.remove(l.id);
            stack.remove(l.id);
            refocus();
            throw e;
        }
        for (Layout p : l.preload) preloadImages(p.root);
    }

    public void close(String id) {
        OverlayWindow w = windows.remove(id);
        stack.remove(id);
        if (w != null) w.close();
        refocus();
    }

    /** For the poll "reset" op: closes every window whose id is not in keep. */
    public void closeAllExcept(Set<String> keep) {
        for (String id : new ArrayList<String>(windows.keySet())) {
            if (keep == null || !keep.contains(id)) close(id);
        }
    }

    public void closeAll() {
        closeAllExcept(null);
    }

    public boolean showing(String id) {
        return windows.containsKey(id);
    }

    /** The newest interactive id still shown holds the remote; the others are made unfocusable. */
    private void refocus() {
        String t = stack.isEmpty() ? null : stack.get(stack.size() - 1);
        for (String id : stack) {
            OverlayWindow w = windows.get(id);
            if (w != null) w.setKeyFocus(id.equals(t));
        }
    }

    private OverlayWindow.Listener relay(final String id) {
        return new OverlayWindow.Listener() {
            @Override public void onAction(Layout l, String action, String elementId, JSONObject values) {
                out.onAction(l, action, elementId, values);
            }

            @Override public void onClosed(Layout l, String reason) {
                // the window closed itself (back, ttl, finished): hand the remote to the next one, then report
                windows.remove(id);
                stack.remove(id);
                refocus();
                out.onClosed(l, reason);
            }

            @Override public void onPassthrough(String key) {
                out.onPassthrough(key);
            }
        };
    }

    private void preloadImages(Node n) {
        if (n == null) return;
        if ("image".equals(n.type) && images != null) images.preload(n.src());
        for (Node c : n.children) preloadImages(c);
    }
}
