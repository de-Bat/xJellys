package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.junit.jupiter.api.Test;

public class PagerTest {
    @Test
    public void tick_advancesAndWraps() {
        Pager p = new Pager(3, 8000);
        assertEquals(0, p.page());
        assertFalse(p.tick(7999));
        assertTrue(p.tick(8001));
        assertEquals(1, p.page());
        assertTrue(p.tick(24001));
        assertEquals(0, p.page());
    }

    @Test
    public void zeroInterval_neverAutoAdvances() {
        Pager p = new Pager(3, 0);
        assertFalse(p.tick(1000000));
        assertEquals(0, p.page());
    }

    @Test
    public void nextPrev_wrap() {
        Pager p = new Pager(3, 0);
        p.prev();
        assertEquals(2, p.page());
        p.next();
        assertEquals(0, p.page());
        p.next();
        assertEquals(1, p.page());
    }

    @Test
    public void manualMove_restartsTimer() {
        Pager p = new Pager(3, 8000);
        p.next();
        assertFalse(p.tick(9000));   // timer restarts at 9000
        assertEquals(1, p.page());
        assertFalse(p.tick(16999));
        assertTrue(p.tick(17001));
        assertEquals(2, p.page());
    }

    @Test
    public void singleOrEmpty_staysAtZero() {
        Pager p = new Pager(1, 1000);
        assertFalse(p.tick(5000));
        p.next();
        assertEquals(0, p.page());
        Pager e = new Pager(0, 1000);
        e.next();
        e.prev();
        assertEquals(0, e.page());
    }
}
