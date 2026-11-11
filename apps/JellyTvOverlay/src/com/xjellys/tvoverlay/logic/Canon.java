package com.xjellys.tvoverlay.logic;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.List;

import org.json.JSONArray;
import org.json.JSONObject;

/** Canonical JSON text (object keys sorted, recursively), so equal JSON compares equal regardless of key order. */
public final class Canon {
    private Canon() {
    }

    public static String of(Object v) {
        StringBuilder b = new StringBuilder();
        write(b, v);
        return b.toString();
    }

    private static void write(StringBuilder b, Object v) {
        if (v instanceof JSONObject) {
            JSONObject o = (JSONObject) v;
            List<String> keys = new ArrayList<String>();
            Iterator<String> it = o.keys();
            while (it.hasNext()) keys.add(it.next());
            Collections.sort(keys);
            b.append('{');
            for (int i = 0; i < keys.size(); i++) {
                if (i > 0) b.append(',');
                b.append(JSONObject.quote(keys.get(i))).append(':');
                write(b, o.opt(keys.get(i)));
            }
            b.append('}');
        } else if (v instanceof JSONArray) {
            JSONArray a = (JSONArray) v;
            b.append('[');
            for (int i = 0; i < a.length(); i++) {
                if (i > 0) b.append(',');
                write(b, a.opt(i));
            }
            b.append(']');
        } else if (v instanceof String) {
            b.append(JSONObject.quote((String) v));
        } else if (v == null || v == JSONObject.NULL) {
            b.append("null");
        } else if (v instanceof Number) {
            try {
                b.append(JSONObject.numberToString((Number) v));
            } catch (org.json.JSONException e) {
                b.append(String.valueOf(v)); // NaN/Infinity: not in parsed JSON
            }
        } else {
            b.append(String.valueOf(v));
        }
    }
}
