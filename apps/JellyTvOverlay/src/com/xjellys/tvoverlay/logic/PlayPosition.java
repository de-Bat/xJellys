package com.xjellys.tvoverlay.logic;

/**
 * The player's position, from the latest /Sessions sample plus elapsed wall time while playing. Ticks are
 * Jellyfin's 100 ns units; times are milliseconds on any monotonic clock. Synchronized: sampled on one thread,
 * read on another.
 */
public final class PlayPosition {
    private static final long TICKS_PER_MS = 10000L;

    private boolean known;
    private long ticks;
    private long at;
    private boolean paused;
    private long runtime;

    /** A sample: position, paused, runtime (0 = unknown), sample time. */
    public synchronized void update(long positionTicks, boolean isPaused, long runtimeTicks, long nowMs) {
        known = true;
        ticks = Math.max(0, positionTicks);
        paused = isPaused;
        runtime = Math.max(0, runtimeTicks);
        at = nowMs;
    }

    public synchronized void clear() {
        known = false;
    }

    public synchronized boolean known() {
        return known;
    }

    public synchronized long estimate(long nowMs) {
        long t = ticks + (paused ? 0 : Math.max(0, nowMs - at) * TICKS_PER_MS);
        return runtime > 0 ? Math.min(t, runtime) : t;
    }

    /** Where a seek by deltaMs from now lands, clamped to [0, runtime]; -1 when the position is unknown. */
    public synchronized long seekTarget(long nowMs, long deltaMs) {
        if (!known) return -1;
        long t = Math.max(0, estimate(nowMs) + deltaMs * TICKS_PER_MS);
        return runtime > 0 ? Math.min(t, runtime) : t;
    }

    /** After sending a seek: the next press counts from there, before the next sample arrives. */
    public synchronized void seekedTo(long targetTicks, long nowMs) {
        if (!known || targetTicks < 0) return;
        ticks = targetTicks;
        at = nowMs;
    }

    /** After sending PlayPause. */
    public synchronized void togglePaused(long nowMs) {
        if (!known) return;
        ticks = estimate(nowMs);
        at = nowMs;
        paused = !paused;
    }
}
