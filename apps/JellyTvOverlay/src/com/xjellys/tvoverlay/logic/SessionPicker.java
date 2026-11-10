package com.xjellys.tvoverlay.logic;

import java.util.Locale;

import org.json.JSONArray;
import org.json.JSONObject;

/** Which Jellyfin session the overlay follows (same rules as JellyXR TV's WatchService.pick). */
public final class SessionPicker {
    private SessionPicker() {
    }

    /**
     * The one the user picked ({@code want}), else a Jellyfin Android TV session on this box (same device name),
     * else any other Android TV session playing, else any other session playing video. Never this app's own
     * session ({@code me}). Null when none.
     */
    public static JSONObject pick(JSONArray list, String me, String want, String box) {
        JSONObject sameBox = null, anyTv = null, any = null;
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
            if (tv && s.optString("DeviceName").equalsIgnoreCase(box)) {
                if (sameBox == null || playing) sameBox = s;
            } else if (tv && playing && anyTv == null) {
                anyTv = s;
            } else if (playing && any == null && isVideo(s)) {
                any = s;
            }
        }
        return sameBox != null ? sameBox : anyTv != null ? anyTv : any;
    }

    private static boolean isVideo(JSONObject s) {
        String t = s.optJSONObject("NowPlayingItem").optString("Type");
        return "Movie".equals(t) || "Episode".equals(t);
    }
}
