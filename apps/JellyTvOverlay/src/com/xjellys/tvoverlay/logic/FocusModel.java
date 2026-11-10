package com.xjellys.tvoverlay.logic;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import com.xjellys.tvoverlay.model.KeyBinding;
import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.model.Node;

/**
 * Local remote-key handling for an interactive layout. Never touches the network.
 *
 * <p>Value stepping: this class does not own a {@link ValueState}. A {@code step} key binding is
 * returned as {@code Kind.STEPPED} with {@code target} and {@code by}; the caller applies them with
 * {@link ValueState#step(String, double)}.
 *
 * <p>Key order: a key binding wins (even for {@code play} in passthrough). Otherwise arrows move
 * focus to the nearest focusable in that direction, judged by the screen rects (not document order,
 * so RTL rows work). Candidates must have their centre beyond the focused centre on the key's axis;
 * score = primary-axis centre distance + 2 x cross-axis centre distance; candidates whose cross-axis
 * ranges overlap the focused rect are preferred (considered first); ties go to document order. With
 * no move possible: a focused {@code scroll} text gives NONE (renderer scrolls); in passthrough,
 * left/right give PASSTHROUGH. {@code ok} on a node with an action gives ACTION, {@code back} gives
 * BACK, {@code play} gives PASSTHROUGH in passthrough layouts. Anything else is NONE. Focusable
 * nodes without an id or without a rect are skipped.
 */
public final class FocusModel {
    public enum Kind { MOVED, STEPPED, ACTION, BACK, PASSTHROUGH, NONE }

    public static final class Result {
        public final Kind kind;
        public final String action;
        public final String elementId;
        /** STEPPED only: the value node id and the amount to add. */
        public final String target;
        public final double by;

        Result(Kind kind, String action, String elementId, String target, double by) {
            this.kind = kind;
            this.action = action;
            this.elementId = elementId;
            this.target = target;
            this.by = by;
        }
    }

    private static final Result NONE = new Result(Kind.NONE, null, null, null, 0);

    private final Layout layout;
    private final Map<String, float[]> rects;
    private final List<Node> order = new ArrayList<Node>();
    private Node focused;

    public FocusModel(Layout l, Map<String, float[]> rects) {
        this.layout = l;
        this.rects = rects;
        if (l.root != null) collect(l.root);
        if (!order.isEmpty()) focused = order.get(0);
    }

    private void collect(Node n) {
        if (n.focusable && n.enabled && n.id != null && rects.get(n.id) != null) order.add(n);
        for (Node c : n.children) collect(c);
    }

    /** Id of the focused node, or null when nothing is focusable. */
    public String focused() {
        return focused == null ? null : focused.id;
    }

    public Result onKey(String key) {
        if (key == null) return NONE;
        String fid = focused();
        KeyBinding b = layout.keys.get(key);
        if (b != null) {
            if ("step".equals(b.op)) return new Result(Kind.STEPPED, null, fid, b.target, b.by);
            if ("action".equals(b.op)) return new Result(Kind.ACTION, b.action, fid, null, 0);
        }
        boolean horizontal = "left".equals(key) || "right".equals(key);
        if (horizontal || "up".equals(key) || "down".equals(key)) {
            Node next = nearest(key);
            if (next != null) {
                focused = next;
                return new Result(Kind.MOVED, null, next.id, null, 0);
            }
            if (focused != null && "text".equals(focused.type) && focused.scroll()) return NONE;
            if (horizontal && layout.passthrough()) return new Result(Kind.PASSTHROUGH, null, fid, null, 0);
            return NONE;
        }
        if ("ok".equals(key)) {
            if (focused != null && focused.action != null) return new Result(Kind.ACTION, focused.action, fid, null, 0);
            return NONE;
        }
        if ("back".equals(key)) return new Result(Kind.BACK, null, fid, null, 0);
        if ("play".equals(key) && layout.passthrough()) return new Result(Kind.PASSTHROUGH, null, fid, null, 0);
        return NONE;
    }

    private Node nearest(String key) {
        if (focused == null) return null;
        float[] f = rects.get(focused.id);
        boolean horiz = "left".equals(key) || "right".equals(key);
        float sign = ("left".equals(key) || "up".equals(key)) ? -1f : 1f;
        float fp = horiz ? (f[0] + f[2]) / 2 : (f[1] + f[3]) / 2;
        float fc = horiz ? (f[1] + f[3]) / 2 : (f[0] + f[2]) / 2;
        Node best = null;
        float bestScore = 0;
        boolean bestOverlap = false;
        for (Node n : order) {
            if (n == focused) continue;
            float[] r = rects.get(n.id);
            float p = horiz ? (r[0] + r[2]) / 2 : (r[1] + r[3]) / 2;
            float c = horiz ? (r[1] + r[3]) / 2 : (r[0] + r[2]) / 2;
            float dp = (p - fp) * sign;
            if (dp <= 0) continue;
            boolean overlap = horiz ? (r[1] < f[3] && r[3] > f[1]) : (r[0] < f[2] && r[2] > f[0]);
            float score = dp + 2f * Math.abs(c - fc);
            if (best == null || (overlap && !bestOverlap) || (overlap == bestOverlap && score < bestScore)) {
                best = n;
                bestScore = score;
                bestOverlap = overlap;
            }
        }
        return best;
    }
}
