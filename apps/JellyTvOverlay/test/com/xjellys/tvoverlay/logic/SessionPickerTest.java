package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.jupiter.api.Test;

public class SessionPickerTest {
    private static JSONObject s(String device, String name, String client, String playType) {
        JSONObject o = new JSONObject().put("DeviceId", device).put("DeviceName", name).put("Client", client)
            .put("Id", "sess-" + device);
        if (playType != null) o.put("NowPlayingItem", new JSONObject().put("Type", playType).put("Name", "n"));
        return o;
    }

    @Test
    public void picksSameBoxAndroidTv_skipsSelf() {
        JSONArray l = new JSONArray()
            .put(s("me", "Living Room", "Android TV", "Movie"))
            .put(s("other", "Bedroom", "Android TV", "Movie"))
            .put(s("tv", "living room", "Jellyfin Android TV", null));
        assertEquals("tv", SessionPicker.pick(l, "me", "", "Living Room").getString("DeviceId"));
    }

    @Test
    public void explicitWatch_onlyThatDevice() {
        JSONArray l = new JSONArray().put(s("tv", "Living Room", "Android TV", "Movie")).put(s("web", "PC", "Jellyfin Web", "Movie"));
        assertEquals("web", SessionPicker.pick(l, "me", "web", "Living Room").getString("DeviceId"));
        assertNull(SessionPicker.pick(l, "me", "gone", "Living Room"));
    }

    @Test
    public void noFallback_toOtherBoxesOrVideoSessions() {
        // R17: never follow another box's player; a box whose name does not match needs one Watch pick
        JSONArray l = new JSONArray().put(s("web", "PC", "Jellyfin Web", "Episode")).put(s("tv2", "Bedroom", "Android TV", "Movie"));
        assertNull(SessionPicker.pick(l, "me", "", "Living Room"));
        assertNull(SessionPicker.pick(l, "me", null, "Living Room"));
        assertNull(SessionPicker.pick(new JSONArray(), "me", "", "x"));
    }

    @Test
    public void sameBox_prefersThePlayingSession() {
        JSONArray l = new JSONArray().put(s("idle", "Living Room", "Android TV", null))
            .put(s("busy", "Living Room", "Jellyfin Android TV", "Movie"));
        assertEquals("busy", SessionPicker.pick(l, "me", "", "Living Room").getString("DeviceId"));
    }
}
