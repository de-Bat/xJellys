package com.xjellys.tvoverlay.logic;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * What to do with one Poll response, in order: reset (close every window not in {@code keep}), show (show or
 * replace by id) or close. A reset keeps the ids of the show ops that follow it with the same seq (the snapshot).
 * Malformed ops are skipped. {@code seq} is the next {@code since}.
 */
public final class PollPlan {
    public enum Kind { RESET, SHOW, CLOSE }

    public static final class Step {
        public final Kind kind;
        /** Layout id (SHOW, CLOSE); null for RESET. */
        public final String id;
        /** The layout JSON (SHOW only). */
        public final JSONObject layout;
        /** Ids to keep (RESET only). */
        public final Set<String> keep;

        Step(Kind kind, String id, JSONObject layout, Set<String> keep) {
            this.kind = kind;
            this.id = id;
            this.layout = layout;
            this.keep = keep;
        }
    }

    public final long seq;
    public final List<Step> steps;

    private PollPlan(long seq, List<Step> steps) {
        this.seq = seq;
        this.steps = Collections.unmodifiableList(steps);
    }

    public static PollPlan of(JSONObject response, long since) {
        long seq = response.optLong("seq", since);
        List<Step> steps = new ArrayList<Step>();
        JSONArray ops = response.optJSONArray("ops");
        if (ops == null) return new PollPlan(seq, steps);

        // first pass: the valid ops
        List<JSONObject> valid = new ArrayList<JSONObject>();
        List<String> ids = new ArrayList<String>();
        for (int i = 0; i < ops.length(); i++) {
            JSONObject o = ops.optJSONObject(i);
            if (o == null) continue;
            String op = o.optString("op", "");
            String id = null;
            if ("show".equals(op)) {
                JSONObject l = o.optJSONObject("layout");
                if (l == null) continue;
                Object lid = l.opt("id");
                id = lid instanceof String && !((String) lid).isEmpty() ? (String) lid : str(o, "layoutId");
                if (id == null) continue;
            } else if ("close".equals(op)) {
                id = str(o, "layoutId");
                if (id == null) continue;
            } else if (!"reset".equals(op)) {
                continue;
            }
            valid.add(o);
            ids.add(id);
        }

        for (int i = 0; i < valid.size(); i++) {
            JSONObject o = valid.get(i);
            String op = o.optString("op");
            if ("reset".equals(op)) {
                long snap = o.optLong("seq", Long.MIN_VALUE);
                Set<String> keep = new HashSet<String>();
                for (int j = i + 1; j < valid.size(); j++) {
                    JSONObject n = valid.get(j);
                    if (!"show".equals(n.optString("op")) || n.optLong("seq", Long.MIN_VALUE) != snap) break;
                    keep.add(ids.get(j));
                }
                steps.add(new Step(Kind.RESET, null, null, keep));
            } else if ("show".equals(op)) {
                steps.add(new Step(Kind.SHOW, ids.get(i), o.optJSONObject("layout"), null));
            } else {
                steps.add(new Step(Kind.CLOSE, ids.get(i), null, null));
            }
        }
        return new PollPlan(seq, steps);
    }

    private static String str(JSONObject o, String k) {
        Object v = o.opt(k);
        return v instanceof String && !((String) v).isEmpty() ? (String) v : null;
    }
}
