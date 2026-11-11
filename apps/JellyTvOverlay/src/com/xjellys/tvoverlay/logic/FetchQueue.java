package com.xjellys.tvoverlay.logic;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Image fetches waiting for a worker: view-bound (foreground) fetches always go before preloads, each FIFO. A
 * preload belongs to a group (the layout id whose window carried it); closing that window drops its queued
 * preloads. A key is queued at most once. Thread-safe.
 */
public final class FetchQueue<T> {
    public static final class Item<T> {
        public final String key;
        public final T payload;

        Item(String key, T payload) {
            this.key = key;
            this.payload = payload;
        }
    }

    private static final class Pre<T> {
        final T payload;
        final String group;

        Pre(T payload, String group) {
            this.payload = payload;
            this.group = group;
        }
    }

    private final LinkedHashMap<String, T> fg = new LinkedHashMap<String, T>();
    private final LinkedHashMap<String, Pre<T>> pre = new LinkedHashMap<String, Pre<T>>();

    /** Queues a view-bound fetch (moves a queued preload of the same key to the front). */
    public synchronized void addForeground(String key, T payload) {
        pre.remove(key);
        if (!fg.containsKey(key)) fg.put(key, payload);
    }

    /** Queues a preload; false when the key is already queued. */
    public synchronized boolean addPreload(String key, T payload, String group) {
        if (fg.containsKey(key) || pre.containsKey(key)) return false;
        pre.put(key, new Pre<T>(payload, group));
        return true;
    }

    /** A view now waits for a queued preload: it becomes a foreground fetch. False when key is not a queued preload. */
    public synchronized boolean promote(String key, T payload) {
        if (pre.remove(key) == null) return false;
        fg.put(key, payload);
        return true;
    }

    /** The next fetch to run, foreground first; null when empty. */
    public synchronized Item<T> next() {
        Iterator<Map.Entry<String, T>> f = fg.entrySet().iterator();
        if (f.hasNext()) {
            Map.Entry<String, T> e = f.next();
            f.remove();
            return new Item<T>(e.getKey(), e.getValue());
        }
        Iterator<Map.Entry<String, Pre<T>>> p = pre.entrySet().iterator();
        if (p.hasNext()) {
            Map.Entry<String, Pre<T>> e = p.next();
            p.remove();
            return new Item<T>(e.getKey(), e.getValue().payload);
        }
        return null;
    }

    /** Drops the group's queued preloads (not running ones); returns their keys in queue order. */
    public synchronized List<String> dropGroup(String group) {
        List<String> out = new ArrayList<String>();
        Iterator<Map.Entry<String, Pre<T>>> p = pre.entrySet().iterator();
        while (p.hasNext()) {
            Map.Entry<String, Pre<T>> e = p.next();
            String g = e.getValue().group;
            if (g == null ? group == null : g.equals(group)) {
                out.add(e.getKey());
                p.remove();
            }
        }
        return out;
    }

    public synchronized int size() {
        return fg.size() + pre.size();
    }
}
