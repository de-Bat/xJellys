package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

public class OverlayEventsTest {
    @Test
    public void action_body() {
        JSONObject v = new JSONObject().put("off", 250);
        JSONObject e = OverlayEvents.action("d1", "JellyXR/actor", "openActor", "p1", v);
        assertEquals("d1", e.getString("deviceId"));
        assertEquals("JellyXR/actor", e.getString("layoutId"));
        assertEquals("action", e.getString("type"));
        assertEquals("openActor", e.getString("action"));
        assertEquals("p1", e.getString("elementId"));
        assertEquals(250, e.getJSONObject("value").getInt("off"));
        assertTrue(e.isNull("reason"));
    }

    @Test
    public void action_nullElementAndValues() {
        JSONObject e = OverlayEvents.action("d1", "a/x", "go", null, null);
        assertTrue(e.isNull("elementId"));
        assertEquals(0, e.getJSONObject("value").length());
    }

    @Test
    public void closed_body() {
        JSONObject e = OverlayEvents.closed("d1", "a/x", "back");
        assertEquals("closed", e.getString("type"));
        assertEquals("back", e.getString("reason"));
        assertEquals("a/x", e.getString("layoutId"));
        assertTrue(e.isNull("action"));
    }
}
