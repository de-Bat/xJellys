package com.xjellys.tvoverlay.model;

import java.util.ArrayList;
import java.util.List;

import org.json.JSONArray;
import org.json.JSONObject;

/** One layout node. Unknown types are kept (type string preserved) and render as nothing. */
public final class Node {
    private static final int MAX_DEPTH = 32;

    public String type = "";
    public String id;
    public String action;
    /** Fractions of the parent box; NaN = flow inside the container. */
    public float x = Float.NaN, y = Float.NaN, w = Float.NaN, h = Float.NaN;
    public float pad, gap, radius;
    public Integer bg;
    public boolean focusable;
    public boolean enabled = true;
    public JSONObject raw;
    public List<Node> children = new ArrayList<Node>();

    private Node() {
    }

    static Node parse(JSONObject o, int depth) {
        Node n = new Node();
        n.raw = o;
        String t = Layout.str(o, "type");
        n.type = t == null ? "" : t;
        n.id = Layout.str(o, "id");
        n.action = Layout.str(o, "action");
        n.x = (float) Layout.num(o, "x", Double.NaN);
        n.y = (float) Layout.num(o, "y", Double.NaN);
        n.w = (float) Layout.num(o, "w", Double.NaN);
        n.h = (float) Layout.num(o, "h", Double.NaN);
        n.pad = (float) Layout.num(o, "pad", 0);
        n.gap = (float) Layout.num(o, "gap", 0);
        n.radius = (float) Layout.num(o, "radius", 0);
        n.bg = parseColor(Layout.str(o, "bg"));
        n.enabled = Layout.bool(o, "enabled", true);
        boolean defFocus = "button".equals(n.type);
        n.focusable = n.enabled && Layout.bool(o, "focusable", defFocus);
        JSONArray c = o.optJSONArray("children");
        if (c != null && depth < MAX_DEPTH) {
            for (int i = 0; i < c.length(); i++) {
                JSONObject co = c.optJSONObject(i);
                if (co != null) n.children.add(parse(co, depth + 1));
            }
        }
        return n;
    }

    /** Parses #AARRGGBB (or #RRGGBB, opaque). Returns null when absent or malformed. */
    static Integer parseColor(String s) {
        if (s == null || s.length() < 2 || s.charAt(0) != '#') return null;
        String hex = s.substring(1);
        if (hex.length() != 6 && hex.length() != 8) return null;
        try {
            long v = Long.parseLong(hex, 16);
            if (hex.length() == 6) v |= 0xFF000000L;
            return Integer.valueOf((int) v);
        } catch (NumberFormatException e) {
            return null;
        }
    }

    // text
    public String text() { return strOr("text", ""); }
    public float size() {
        double d = Layout.num(raw, "size", 0.03);
        return d > 0 ? (float) d : 0.03f;
    }
    public Integer color() { return parseColor(Layout.str(raw, "color")); }
    public boolean bold() { return Layout.bool(raw, "bold", false); }
    /** 0 = unlimited. */
    public int maxLines() { return (int) Math.max(0, Layout.num(raw, "maxLines", 0)); }
    public String dir() {
        String d = Layout.str(raw, "dir");
        return ("ltr".equals(d) || "rtl".equals(d)) ? d : "auto";
    }
    public boolean scroll() { return Layout.bool(raw, "scroll", false); }

    // image
    public String src() { return strOr("src", ""); }
    public String fit() { return "contain".equals(Layout.str(raw, "fit")) ? "contain" : "cover"; }
    public boolean round() { return Layout.bool(raw, "round", false); }

    // icon node uses "name"; button uses "icon"
    public String icon() { return strOr("icon".equals(type) ? "name" : "icon", ""); }
    public String label() { return strOr("label", ""); }

    // list
    public String orientation() {
        return "horizontal".equals(Layout.str(raw, "orientation")) ? "horizontal" : "vertical";
    }

    // pages / sequence
    public int intervalMs() { return (int) Math.max(0, Layout.num(raw, "intervalMs", 0)); }
    public int durationMs() { return (int) Math.max(0, Layout.num(raw, "durationMs", 0)); }
    public String finishedAction() { return Layout.str(raw, "finishedAction"); }

    // value
    public double value() { return Layout.num(raw, "value", 0); }
    public double min() { return Layout.num(raw, "min", Double.NEGATIVE_INFINITY); }
    public double max() { return Layout.num(raw, "max", Double.POSITIVE_INFINITY); }
    public double scale() {
        double s = Layout.num(raw, "scale", 1);
        return s == 0 ? 1 : s;
    }
    public int decimals() { return (int) Math.max(0, Layout.num(raw, "decimals", 0)); }
    public String suffix() { return strOr("suffix", ""); }
    public boolean signed() { return Layout.bool(raw, "signed", false); }
    public String changedAction() { return Layout.str(raw, "changedAction"); }

    private String strOr(String k, String def) {
        String s = Layout.str(raw, k);
        return s == null ? def : s;
    }
}
