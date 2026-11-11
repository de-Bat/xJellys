package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.junit.jupiter.api.Test;

public class SequencerTest {
    @Test
    public void index_byElapsed() {
        Sequencer s = new Sequencer(new int[] {3000, 5000});
        assertEquals(0, s.index(0));
        assertEquals(0, s.index(2999));
        assertEquals(1, s.index(3000));
        assertEquals(1, s.index(3500));
        assertEquals(-1, s.index(8000));
        assertEquals(0, s.index(-5));
    }

    @Test
    public void zeroDurationSkipped_andEmptyFinished() {
        assertEquals(2, new Sequencer(new int[] {0, 0, 10}).index(0));
        assertEquals(-1, new Sequencer(new int[0]).index(0));
    }
}
