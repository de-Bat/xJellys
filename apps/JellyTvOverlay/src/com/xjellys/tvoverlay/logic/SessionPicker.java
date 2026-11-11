package com.xjellys.tvoverlay.logic;

import java.util.Locale;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Which Jellyfin session the overlay follows: the Jellyfin Android TV session on this box (same device name), or
 * the one the user picked with Watch. Never another box's player: overlays and the remote belong to this box.
 */
public final class SessionPicker {
    private SessionPicker() {
    }

    /**
     * The one the user picked ({@code want}), else a Jellyfin Android TV session on this box (same device name,
     * a playing one first). Never this app's own session ({@code me}). Null when none.
     */
    public static JSONObject pick(JSONArray list, String me, String want, String box) {
        JSONObject sameBox = null;
        for (int i = 0; i < list.length(); i++) {
            JSONObject s = list.optJSONObject(i);
            if (s == null) continue;
            String id = s.optString("DeviceId");
            if (id.equals(me)) continue;
            if (want != null && !want.isEmpty()) {
                if (id.equals(want)) return s;
                continue;
            }
            boolean tv = s.optString("Client").toLowerCase(Locale.ROOT).contains("android tv");
            boolean playing = s.optJSONObject("NowPlayingItem") != null;
            if (tv && box != null && s.optString("DeviceName").equalsIgnoreCase(box)) {
                if (sameBox == null || playing) sameBox = s;
            }
        }
        return sameBox;
    }
}
