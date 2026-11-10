package com.xjellys.tvoverlay.model;

/** A remote key binding: op "action" (report action) or "step" (add by to value node target). */
public final class KeyBinding {
    public final String op;
    public final String action;
    public final String target;
    public final double by;

    public KeyBinding(String op, String action, String target, double by) {
        this.op = op;
        this.action = action;
        this.target = target;
        this.by = by;
    }
}
