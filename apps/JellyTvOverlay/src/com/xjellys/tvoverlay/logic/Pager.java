package com.xjellys.tvoverlay.logic;

/**
 * Page index with optional auto-advance. tick(nowMs) takes time on a clock whose zero is when the
 * pages were shown; it advances one page per full interval elapsed (a late tick can skip pages).
 * A manual next()/prev() restarts the interval at the next tick.
 */
public final class Pager {
    private final int count;
    private final int intervalMs;
    private int page;
    private long lastMs;
    private boolean restart;

    public Pager(int count, int intervalMs) {
        this.count = Math.max(0, count);
        this.intervalMs = Math.max(0, intervalMs);
    }

    public int page() {
        return page;
    }

    /** Returns true if the page changed. */
    public boolean tick(long nowMs) {
        if (restart) {
            restart = false;
            lastMs = nowMs;
            return false;
        }
        if (intervalMs <= 0 || count <= 1 || nowMs - lastMs < intervalMs) return false;
        long steps = (nowMs - lastMs) / intervalMs;
        lastMs += steps * intervalMs;
        int old = page;
        page = (int) ((page + steps) % count);
        return page != old;
    }

    public void next() {
        move(1);
    }

    public void prev() {
        move(-1);
    }

    private void move(int d) {
        if (count <= 1) return;
        page = (page + d + count) % count;
        restart = true;
    }
}
