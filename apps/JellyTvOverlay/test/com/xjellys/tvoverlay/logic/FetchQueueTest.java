package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;

import org.junit.jupiter.api.Test;

public class FetchQueueTest {
    @Test
    public void foreground_jumpsAheadOfQueuedPreloads() {
        FetchQueue<Integer> q = new FetchQueue<Integer>();
        assertTrue(q.addPreload("p1", 1, "g"));
        assertTrue(q.addPreload("p2", 2, "g"));
        q.addForeground("v1", 3);
        assertEquals("v1", q.next().key);
        assertEquals("p1", q.next().key);
        q.addForeground("v2", 4);
        assertEquals("v2", q.next().key);
        assertEquals("p2", q.next().key);
        assertNull(q.next());
    }

    @Test
    public void promote_movesAQueuedPreloadToTheFront() {
        FetchQueue<Integer> q = new FetchQueue<Integer>();
        q.addPreload("p1", 1, "g");
        q.addPreload("p2", 2, "g");
        assertTrue(q.promote("p2", 20));
        assertFalse(q.promote("nope", 0));
        FetchQueue.Item<Integer> first = q.next();
        assertEquals("p2", first.key);
        assertEquals(Integer.valueOf(20), first.payload);
        // a promoted fetch is no longer a preload: closing the group keeps it
        q.addPreload("p3", 3, "g");
        assertTrue(q.promote("p3", 3));
        assertEquals(Arrays.asList("p1"), q.dropGroup("g"));
        assertEquals("p3", q.next().key);
        assertNull(q.next());
    }

    @Test
    public void dropGroup_removesOnlyThatGroupsQueuedPreloads() {
        FetchQueue<Integer> q = new FetchQueue<Integer>();
        q.addPreload("a1", 1, "a");
        q.addPreload("b1", 2, "b");
        q.addPreload("a2", 3, "a");
        q.addForeground("v", 4);
        assertEquals(Arrays.asList("a1", "a2"), q.dropGroup("a"));
        assertTrue(q.dropGroup("a").isEmpty());
        assertEquals("v", q.next().key);
        assertEquals("b1", q.next().key);
        assertNull(q.next());
        assertEquals(0, q.size());
    }

    @Test
    public void duplicates_areNotQueuedTwice() {
        FetchQueue<Integer> q = new FetchQueue<Integer>();
        assertTrue(q.addPreload("x", 1, "g"));
        assertFalse(q.addPreload("x", 1, "h"));
        q.addForeground("y", 2);
        assertFalse(q.addPreload("y", 2, "g"));
        q.addForeground("y", 2);
        assertEquals(2, q.size());
    }
}
