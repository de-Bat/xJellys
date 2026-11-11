package com.xjellys.tvoverlay.logic;

/** Maps elapsed time to the active step of a sequence; -1 once every step has finished. */
public final class Sequencer {
    private final int[] durationsMs;

    public Sequencer(int[] durationsMs) {
        this.durationsMs = durationsMs == null ? new int[0] : durationsMs.clone();
    }

    public int index(long elapsedMs) {
        long t = Math.max(0, elapsedMs);
        long end = 0;
        for (int i = 0; i < durationsMs.length; i++) {
            end += Math.max(0, durationsMs[i]);
            if (t < end) return i;
        }
        return -1;
    }
}
