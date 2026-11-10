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
    public void fallbacks_anyPlayingTv_thenAnyVideo() {
        JSONArray l = new JSONArray().put(s("web", "PC", "Jellyfin Web", "Episode")).put(s("tv2", "Bedroom", "Android TV", "Movie"));
        assertEquals("tv2", SessionPicker.pick(l, "me", "", "Living Room").getString("DeviceId"));
        JSONArray l2 = new JSONArray().put(s("web", "PC", "Jellyfin Web", "Audio")).put(s("web2", "PC2", "Jellyfin Web", "Episode"));
        assertEquals("web2", SessionPicker.pick(l2, "me", "", "Living Room").getString("DeviceId"));
        assertNull(SessionPicker.pick(new JSONArray(), "me", "", "x"));
    }
}
