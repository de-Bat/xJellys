package com.xjellys.tvoverlay;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

public class SmokeTest {
    @Test
    public void harnessRunsAndJsonIsOnClasspath() {
        assertEquals(1, new JSONObject("{\"v\":1}").getInt("v"));
    }
}
