package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Set;

import org.junit.jupiter.api.Test;

public class LocalClosesTest {
    private static Set<String> set(String... s) {
        return new HashSet<String>(Arrays.asList(s));
    }

    @Test
    public void snapshotShow_ofLocallyClosedId_isSkipped() {
        LocalCloses c = new LocalCloses();
        c.closed("a/x", 1000);
        c.onReset(set("a/x", "b/y"), 2000);
        assertFalse(c.allowShow("a/x", true, 2000));
        assertTrue(c.allowShow("b/y", true, 2000));
    }

    @Test
    public void skippedFor60sOnly() {
        LocalCloses c = new LocalCloses();
        c.closed("a/x", 1000);
        c.onReset(set("a/x"), 1000 + LocalCloses.SKIP_MS - 1);
        assertFalse(c.allowShow("a/x", true, 1000 + LocalCloses.SKIP_MS - 1));
        c.onReset(set("a/x"), 1000 + LocalCloses.SKIP_MS);
        assertTrue(c.allowShow("a/x", true, 1000 + LocalCloses.SKIP_MS));
        assertFalse(c.has("a/x"));
    }

    @Test
    public void forgotten_whenASnapshotNoLongerListsIt() {
        LocalCloses c = new LocalCloses();
        c.closed("a/x", 1000);
        c.onReset(Collections.<String>emptySet(), 1500);
        assertFalse(c.has("a/x"));
        c.onReset(set("a/x"), 1600);
        assertTrue(c.allowShow("a/x", true, 1600));
    }

    @Test
    public void incrementalShow_isAllowed_andForgets() {
        // a show outside a snapshot is a new show by the plugin, not the host replaying the old one
        LocalCloses c = new LocalCloses();
        c.closed("a/x", 1000);
        assertTrue(c.allowShow("a/x", false, 1100));
        assertFalse(c.has("a/x"));
    }

    @Test
    public void hostClose_forgets() {
        LocalCloses c = new LocalCloses();
        c.closed("a/x", 1000);
        c.hostClosed("a/x");
        assertFalse(c.has("a/x"));
    }

    @Test
    public void nullIds_ignored() {
        LocalCloses c = new LocalCloses();
        c.closed(null, 1);
        assertTrue(c.allowShow(null, true, 1));
        c.onReset(null, 1);
    }

    @Test
    public void retryDelays_1_2_4_thenStop() {
        assertEquals(1000, LocalCloses.retryDelayMs(0));
        assertEquals(2000, LocalCloses.retryDelayMs(1));
        assertEquals(4000, LocalCloses.retryDelayMs(2));
        assertEquals(-1, LocalCloses.retryDelayMs(3));
        assertEquals(-1, LocalCloses.retryDelayMs(-1));
    }

    @Test
    public void retryable_networkAnd5xxOnly() {
        assertTrue(LocalCloses.retryable(0));
        assertTrue(LocalCloses.retryable(500));
        assertTrue(LocalCloses.retryable(503));
        assertFalse(LocalCloses.retryable(403));
        assertFalse(LocalCloses.retryable(400));
        assertFalse(LocalCloses.retryable(401));
    }
}
