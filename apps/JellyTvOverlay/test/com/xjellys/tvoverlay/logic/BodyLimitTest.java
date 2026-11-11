package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.io.ByteArrayInputStream;
import java.io.IOException;

import org.junit.jupiter.api.Test;

public class BodyLimitTest {
    @Test
    public void underCap_readsAll() throws Exception {
        assertEquals(10, BodyLimit.read(new ByteArrayInputStream(new byte[10]), 10).length);
    }

    @Test
    public void overCap_failsAsIo() {
        assertThrows(IOException.class, new org.junit.jupiter.api.function.Executable() {
            @Override public void execute() throws Throwable {
                BodyLimit.read(new ByteArrayInputStream(new byte[20000]), 10000);
            }
        });
    }

    @Test
    public void pollCap_is4MB() {
        assertEquals(4 * 1024 * 1024, BodyLimit.POLL_MAX_BYTES);
    }
}
