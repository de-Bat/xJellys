package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.junit.jupiter.api.Test;

public class PlayPositionTest {
    private static final long S = 10000000L; // ticks per second

    @Test
    public void unknown_noTarget() {
        PlayPosition p = new PlayPosition();
        assertFalse(p.known());
        assertEquals(-1, p.seekTarget(1000, 10000));
    }

    @Test
    public void playing_extrapolatesWallTime() {
        PlayPosition p = new PlayPosition();
        p.update(60 * S, false, 0, 1000);
        assertEquals(63 * S, p.estimate(4000));
        assertEquals(73 * S, p.seekTarget(4000, 10000));
        assertEquals(53 * S, p.seekTarget(4000, -10000));
    }

    @Test
    public void paused_staysPut() {
        PlayPosition p = new PlayPosition();
        p.update(60 * S, true, 0, 1000);
        assertEquals(60 * S, p.estimate(9000));
    }

    @Test
    public void seek_clampsToStartAndRuntime() {
        PlayPosition p = new PlayPosition();
        p.update(5 * S, true, 100 * S, 0);
        assertEquals(0, p.seekTarget(0, -10000));
        p.update(95 * S, true, 100 * S, 0);
        assertEquals(100 * S, p.seekTarget(0, 10000));
    }

    @Test
    public void playing_extrapolationCappedAtRuntime() {
        PlayPosition p = new PlayPosition();
        p.update(95 * S, false, 100 * S, 0);
        assertEquals(100 * S, p.estimate(60000));
    }

    @Test
    public void seekedTo_repeatedPressesAccumulate() {
        PlayPosition p = new PlayPosition();
        p.update(60 * S, true, 0, 0);
        p.seekedTo(p.seekTarget(100, 10000), 100);
        assertEquals(80 * S, p.seekTarget(200, 10000));
    }

    @Test
    public void togglePause_freezesAndResumes() {
        PlayPosition p = new PlayPosition();
        p.update(60 * S, false, 0, 0);
        p.togglePaused(2000);
        assertEquals(62 * S, p.estimate(10000));
        p.togglePaused(10000);
        assertEquals(63 * S, p.estimate(11000));
    }

    @Test
    public void clear_forgets() {
        PlayPosition p = new PlayPosition();
        p.update(60 * S, false, 0, 0);
        p.clear();
        assertFalse(p.known());
    }
}
