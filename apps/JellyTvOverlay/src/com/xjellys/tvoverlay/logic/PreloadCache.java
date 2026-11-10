package com.xjellys.tvoverlay.logic;

import java.util.LinkedHashMap;
import java.util.Map;

import org.json.JSONArray;
import org.json.JSONObject;

import com.xjellys.tvoverlay.model.Layout;

/**
 * Parsed {@code preload} layouts by id, with their canonical JSON. A later show for that id whose JSON is the same
 * gets the cached Layout (its images were preloaded with it). Bounded, oldest out. Not thread-safe (main thread).
 */
public final class PreloadCache {
    private static final class Entry {
        final String canon;
        final Layout layout;

        Entry(String canon, Layout layout) {
            this.canon = canon;
            this.layout = layout;
        }
    }

    private final int max;
    private final Map<String, Entry> byId = new LinkedHashMap<String, Entry>();

    public PreloadCache(int max) {
        this.max = Math.max(1, max);
    }

    /** Caches the preload entries of a shown layout: raw = its JSON, parsed = Layout.parse(raw). */
    public void addFrom(JSONObject raw, Layout parsed) {
        JSONArray pre = raw == null ? null : raw.optJSONArray("preload");
        if (pre == null || parsed == null) return;
        for (int i = 0; i < pre.length(); i++) {
            JSONObject po = pre.optJSONObject(i);
            if (po == null) continue;
            Object id = po.opt("id");
            if (!(id instanceof String)) continue;
            for (Layout l : parsed.preload) {
                if (id.equals(l.id)) {
                    byId.remove(l.id);
                    byId.put(l.id, new Entry(Canon.of(po), l));
                    break;
                }
            }
        }
        while (byId.size() > max) byId.remove(byId.keySet().iterator().next());
    }

    /** The cached Layout for this show JSON when its id is cached with identical JSON, else null. */
    public Layout take(JSONObject layoutJson) {
        Object id = layoutJson == null ? null : layoutJson.opt("id");
        if (!(id instanceof String)) return null;
        Entry e = byId.get(id);
        return e != null && e.canon.equals(Canon.of(layoutJson)) ? e.layout : null;
    }

    public int size() {
        return byId.size();
    }

    public void clear() {
        byId.clear();
    }
}
