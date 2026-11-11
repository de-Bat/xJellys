package com.xjellys.tvoverlay.logic;

import java.util.LinkedHashMap;
import java.util.Map;

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
        addFrom(PreparedShow.of(raw), parsed);
    }

    /**
     * Caches the preload entries of a shown layout, with the canonical JSON computed off the main thread.
     * shown = the Layout actually shown (p.layout, or a cached one with the same JSON).
     */
    public void addFrom(PreparedShow p, Layout shown) {
        if (p == null || shown == null) return;
        for (Layout l : shown.preload) {
            String canon = p.preloadCanons.get(l.id);
            if (canon == null) continue;
            byId.remove(l.id);
            byId.put(l.id, new Entry(canon, l));
        }
        while (byId.size() > max) byId.remove(byId.keySet().iterator().next());
    }

    /** The cached Layout for this show JSON when its id is cached with identical JSON, else null. */
    public Layout take(JSONObject layoutJson) {
        return layoutJson == null ? null : take(PreparedShow.of(layoutJson));
    }

    /** As take(JSONObject), with the canonical JSON already computed. */
    public Layout take(PreparedShow p) {
        Object id = p == null || p.json == null ? null : p.json.opt("id");
        if (!(id instanceof String)) return null;
        Entry e = byId.get(id);
        return e != null && e.canon.equals(p.canon) ? e.layout : null;
    }

    public int size() {
        return byId.size();
    }

    public void clear() {
        byId.clear();
    }
}
