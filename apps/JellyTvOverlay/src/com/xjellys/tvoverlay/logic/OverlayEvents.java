package com.xjellys.tvoverlay.logic;

import org.json.JSONException;
import org.json.JSONObject;

/** Bodies for POST /JellyTvOverlay/Event (layout-v1 "App to host"). */
public final class OverlayEvents {
    private OverlayEvents() {
    }

    public static JSONObject action(String deviceId, String layoutId, String action, String elementId, JSONObject values) {
        return body(deviceId, layoutId, "action", action, elementId, values, null);
    }

    public static JSONObject closed(String deviceId, String layoutId, String reason) {
        return body(deviceId, layoutId, "closed", null, null, null, reason);
    }

    private static JSONObject body(String deviceId, String layoutId, String type, String action, String elementId,
                                   JSONObject values, String reason) {
        try {
            return new JSONObject()
                .put("deviceId", deviceId)
                .put("layoutId", layoutId)
                .put("type", type)
                .put("action", action == null ? JSONObject.NULL : action)
                .put("elementId", elementId == null ? JSONObject.NULL : elementId)
                .put("value", values == null ? new JSONObject() : values)
                .put("reason", reason == null ? JSONObject.NULL : reason);
        } catch (JSONException e) {
            throw new IllegalStateException(e); // only for non-finite numbers; none here
        }
    }
}
