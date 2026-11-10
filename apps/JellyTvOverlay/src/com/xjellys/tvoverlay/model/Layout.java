package com.xjellys.tvoverlay.model;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/** Layout v1 document. Parsing is total except for a missing id/root or wrong version. */
public final class Layout {
    public String id;
    public String owner;
    public String focus = "passive";
    public String parent;
    public int ttlMs;
    public float dim;
    public List<String> closeOn = new ArrayList<String>();
    public List<String> suppressTriggers = new ArrayList<String>();
    public Map<String, KeyBinding> keys = new LinkedHashMap<String, KeyBinding>();
    public List<Layout> preload = new ArrayList<Layout>();
    public Node root;

    private Layout() {
    }

    public static Layout parse(JSONObject o) throws JSONException {
        if (o == null) throw new JSONException("layout is null");
        if (o.optInt("v", -1) != 1) throw new JSONException("unsupported layout version");
        Object idv = o.opt("id");
        if (!(idv instanceof String) || ((String) idv).isEmpty()) throw new JSONException("layout id missing");
        JSONObject rootObj = o.optJSONObject("root");
        if (rootObj == null) throw new JSONException("layout root missing");

        Layout l = new Layout();
        l.id = (String) idv;
        l.owner = str(o, "owner");
        l.parent = str(o, "parent");
        String f = str(o, "focus");
        l.focus = ("interactive".equals(f) || "interactive+passthrough".equals(f)) ? f : "passive";
        l.ttlMs = (int) Math.max(0, num(o, "ttlMs", 0));
        l.dim = (float) Math.max(0, Math.min(1, num(o, "dim", 0)));

        List<String> close = strList(o.opt("closeOn"));
        if (close == null) {
            l.closeOn.add("stop");
            l.closeOn.add("itemStart");
        } else {
            l.closeOn = close;
        }
        List<String> sup = strList(o.opt("suppressTriggers"));
        if (sup != null) l.suppressTriggers = sup;

        JSONObject keys = o.optJSONObject("keys");
        if (keys != null) {
            Iterator<String> it = keys.keys();
            while (it.hasNext()) {
                String k = it.next();
                JSONObject b = keys.optJSONObject(k);
                if (b == null) continue;
                String op = str(b, "op");
                if ("action".equals(op)) {
                    l.keys.put(k, new KeyBinding(op, str(b, "action"), null, 0));
                } else if ("step".equals(op)) {
                    l.keys.put(k, new KeyBinding(op, null, str(b, "target"), num(b, "by", 0)));
                }
            }
        }

        JSONArray pre = o.optJSONArray("preload");
        if (pre != null) {
            for (int i = 0; i < pre.length(); i++) {
                JSONObject po = pre.optJSONObject(i);
                if (po == null) continue;
                try {
                    l.preload.add(parse(po));
                } catch (JSONException skip) {
                    // an invalid preload entry is dropped, not fatal
                }
            }
        }

        l.root = Node.parse(rootObj, 0);
        return l;
    }

    public boolean interactive() {
        return "interactive".equals(focus) || "interactive+passthrough".equals(focus);
    }

    public boolean passthrough() {
        return "interactive+passthrough".equals(focus);
    }

    static String str(JSONObject o, String k) {
        Object v = o.opt(k);
        return v instanceof String ? (String) v : null;
    }

    static double num(JSONObject o, String k, double def) {
        Object v = o.opt(k);
        if (v instanceof Number) {
            double d = ((Number) v).doubleValue();
            return (Double.isNaN(d) || Double.isInfinite(d)) ? def : d;
        }
        return def;
    }

    static boolean bool(JSONObject o, String k, boolean def) {
        Object v = o.opt(k);
        return v instanceof Boolean ? ((Boolean) v).booleanValue() : def;
    }

    /** Returns null when the value is not an array; non-string items are skipped. */
    private static List<String> strList(Object v) {
        if (!(v instanceof JSONArray)) return null;
        JSONArray a = (JSONArray) v;
        List<String> out = new ArrayList<String>();
        for (int i = 0; i < a.length(); i++) {
            Object e = a.opt(i);
            if (e instanceof String) out.add((String) e);
        }
        return out;
    }
}
