package com.xjellys.tvoverlay.logic;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import com.xjellys.tvoverlay.model.Layout;

/**
 * A show op's layout, parsed and canonicalized off the main thread (on the poll thread) so the main thread only
 * applies it. {@code layout} is null (with {@code error}) when the JSON is not a valid layout.
 */
public final class PreparedShow {
    public final JSONObject json;
    public final Layout layout;
    public final String error;
    /** Canonical JSON of the whole layout (preload cache key). */
    public final String canon;
    /** Canonical JSON of each preload entry, by its id. */
    public final Map<String, String> preloadCanons;

    private PreparedShow(JSONObject json, Layout layout, String error, String canon, Map<String, String> preloadCanons) {
        this.json = json;
        this.layout = layout;
        this.error = error;
        this.canon = canon;
        this.preloadCanons = preloadCanons;
    }

    public static PreparedShow of(JSONObject json) {
        Layout l = null;
        String err = null;
        try {
            l = Layout.parse(json);
        } catch (JSONException e) {
            err = e.getMessage();
        }
        Map<String, String> pc = new HashMap<String, String>();
        JSONArray pre = json == null ? null : json.optJSONArray("preload");
        if (pre != null && l != null) {
            for (int i = 0; i < pre.length(); i++) {
                JSONObject po = pre.optJSONObject(i);
                Object id = po == null ? null : po.opt("id");
                if (id instanceof String) pc.put((String) id, Canon.of(po));
            }
        }
        return new PreparedShow(json, l, err, json == null ? "" : Canon.of(json), Collections.unmodifiableMap(pc));
    }
}
