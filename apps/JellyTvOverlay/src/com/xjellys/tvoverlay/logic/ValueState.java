package com.xjellys.tvoverlay.logic;

import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

import org.json.JSONException;
import org.json.JSONObject;

import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.model.Node;

/** Current values of the layout's {@code value} nodes (by id). Local only. */
public final class ValueState {
    private final Map<String, Node> nodes = new LinkedHashMap<String, Node>();
    private final Map<String, Double> values = new LinkedHashMap<String, Double>();

    public ValueState(Layout l) {
        if (l.root != null) collect(l.root);
    }

    private void collect(Node n) {
        if ("value".equals(n.type) && n.id != null && !nodes.containsKey(n.id)) {
            nodes.put(n.id, n);
            values.put(n.id, Double.valueOf(clamp(n, n.value())));
        }
        for (Node c : n.children) collect(c);
    }

    private static double clamp(Node n, double v) {
        return Math.max(n.min(), Math.min(n.max(), v));
    }

    /** Current raw value; 0 for an unknown id. */
    public double get(String id) {
        Double v = id == null ? null : values.get(id);
        return v == null ? 0 : v.doubleValue();
    }

    /** Adds by, clamped to min/max. Returns true when the value changed. */
    public boolean step(String id, double by) {
        Node n = id == null ? null : nodes.get(id);
        if (n == null || Double.isNaN(by) || Double.isInfinite(by)) return false;
        double old = get(id);
        double nv = clamp(n, old + by);
        values.put(id, Double.valueOf(nv));
        return nv != old;
    }

    /** value/scale with decimals, a leading + when signed and non-negative, then the suffix. */
    public String display(String id) {
        Node n = id == null ? null : nodes.get(id);
        if (n == null) return "";
        String s = String.format(Locale.ROOT, "%." + n.decimals() + "f", get(id) / n.scale());
        if (s.startsWith("-") && s.replaceAll("[-0.]", "").isEmpty()) s = s.substring(1);
        if (n.signed() && !s.startsWith("-")) s = "+" + s;
        return s + n.suffix();
    }

    /** id to current raw value, for event payloads. */
    public JSONObject snapshot() {
        JSONObject o = new JSONObject();
        for (Map.Entry<String, Double> e : values.entrySet()) {
            try {
                o.put(e.getKey(), e.getValue().doubleValue());
            } catch (JSONException ignored) {
                // cannot occur: values are finite
            }
        }
        return o;
    }
}
