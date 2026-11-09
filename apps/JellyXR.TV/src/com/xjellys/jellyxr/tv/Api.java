package com.xjellys.jellyxr.tv;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;

/** Minimal Jellyfin REST client. Every call blocks: run it off the main thread. */
final class Api {
    static final String CLIENT = "JellyXR TV";

    /** HTTP status other than 2xx. */
    static final class HttpError extends IOException {
        final int code;

        HttpError(int code, String msg) {
            super("HTTP " + code + (msg == null || msg.isEmpty() ? "" : ": " + msg));
            this.code = code;
        }
    }

    private final String base;
    private final String deviceName;
    private final String deviceId;
    private final String token;

    Api(String server, String deviceName, String deviceId, String token) {
        this.base = normalize(server);
        this.deviceName = deviceName;
        this.deviceId = deviceId;
        this.token = token;
    }

    static Api of(Prefs p) {
        return new Api(p.server(), p.deviceName(), p.deviceId(), p.token());
    }

    /** "192.168.1.5" -> "http://192.168.1.5:8096", strips trailing slashes. */
    static String normalize(String server) {
        String s = server == null ? "" : server.trim();
        while (s.endsWith("/")) s = s.substring(0, s.length() - 1);
        if (s.isEmpty()) return s;
        if (!s.contains("://")) {
            s = "http://" + s;
            if (s.indexOf(':', 7) < 0) s += ":8096";
        }
        return s;
    }

    String base() {
        return base;
    }

    private String auth() {
        StringBuilder b = new StringBuilder("MediaBrowser Client=\"").append(CLIENT)
            .append("\", Device=\"").append(enc(deviceName))
            .append("\", DeviceId=\"").append(enc(deviceId))
            .append("\", Version=\"").append(BuildInfo.VERSION).append('"');
        if (token != null && !token.isEmpty()) b.append(", Token=\"").append(token).append('"');
        return b.toString();
    }

    static String enc(String s) {
        try {
            return URLEncoder.encode(s == null ? "" : s, "UTF-8").replace("+", "%20");
        } catch (Exception e) {
            return "";
        }
    }

    private HttpURLConnection open(String method, String path) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(base + path).openConnection();
        c.setRequestMethod(method);
        c.setConnectTimeout(5000);
        c.setReadTimeout(10000);
        c.setRequestProperty("Authorization", auth());
        c.setRequestProperty("Accept", "application/json");
        return c;
    }

    private byte[] call(String method, String path, String jsonBody) throws IOException {
        try {
            return callOnce(method, path, jsonBody);
        } catch (HttpError e) {
            throw e;
        } catch (IOException e) {
            // a pooled keep-alive connection the server already closed: retry reads once on a fresh one
            if (!"GET".equals(method)) throw e;
            return callOnce(method, path, jsonBody);
        }
    }

    private byte[] callOnce(String method, String path, String jsonBody) throws IOException {
        HttpURLConnection c = open(method, path);
        try {
            if (jsonBody != null) {
                c.setDoOutput(true);
                c.setRequestProperty("Content-Type", "application/json");
                OutputStream o = c.getOutputStream();
                o.write(jsonBody.getBytes(StandardCharsets.UTF_8));
                o.close();
            } else if ("POST".equals(method)) {
                c.setDoOutput(true);
                c.setFixedLengthStreamingMode(0);
                c.getOutputStream().close();
            }
            int code = c.getResponseCode();
            if (code < 200 || code >= 300) {
                InputStream err = c.getErrorStream();
                String msg = err == null ? "" : new String(read(err), StandardCharsets.UTF_8);
                if (msg.length() > 160) msg = msg.substring(0, 160);
                throw new HttpError(code, msg.trim());
            }
            if (code == 204) return new byte[0];
            return read(c.getInputStream());
        } finally {
            c.disconnect();
        }
    }

    private static byte[] read(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        in.close();
        return out.toByteArray();
    }

    private String text(String method, String path, String body) throws IOException {
        return new String(call(method, path, body), StandardCharsets.UTF_8);
    }

    /** Server name, or throws if the URL is not a Jellyfin server. */
    String serverName() throws Exception {
        return new JSONObject(text("GET", "/System/Info/Public", null)).optString("ServerName", "Jellyfin");
    }

    boolean quickConnectEnabled() {
        try {
            return "true".equalsIgnoreCase(text("GET", "/QuickConnect/Enabled", null).trim());
        } catch (Exception e) {
            return false;
        }
    }

    /** Returns {secret, code}. */
    String[] quickConnectStart() throws Exception {
        JSONObject o = new JSONObject(text("POST", "/QuickConnect/Initiate", null));
        return new String[] {o.getString("Secret"), o.getString("Code")};
    }

    boolean quickConnectApproved(String secret) throws Exception {
        return new JSONObject(text("GET", "/QuickConnect/Connect?secret=" + enc(secret), null)).optBoolean("Authenticated");
    }

    /** Returns {token, userId, userName}. */
    String[] quickConnectFinish(String secret) throws Exception {
        JSONObject body = new JSONObject().put("Secret", secret);
        return session(new JSONObject(text("POST", "/Users/AuthenticateWithQuickConnect", body.toString())));
    }

    /** Returns {token, userId, userName}. */
    String[] signIn(String user, String password) throws Exception {
        JSONObject body = new JSONObject().put("Username", user).put("Pw", password == null ? "" : password);
        return session(new JSONObject(text("POST", "/Users/AuthenticateByName", body.toString())));
    }

    private static String[] session(JSONObject r) throws Exception {
        JSONObject u = r.getJSONObject("User");
        return new String[] {r.getString("AccessToken"), u.getString("Id"), u.optString("Name")};
    }

    void signOut() {
        try {
            call("POST", "/Sessions/Logout", null);
        } catch (Exception ignored) {
            // token is dropped locally anyway
        }
    }

    JSONArray sessions() throws Exception {
        return new JSONArray(text("GET", "/Sessions?activeWithinSeconds=900", null));
    }

    /** Pause cards from the JellyXR plugin, or null when there is nothing to show (204). */
    JSONObject cards(String itemId, long positionTicks) throws Exception {
        String t = text("GET", "/JellyXR/Cards/" + enc(itemId) + "?positionTicks=" + positionTicks + "&source=tv", null);
        return t.trim().isEmpty() ? null : new JSONObject(t);
    }

    /** Tells the plugin which playback device this app covers. False = panel off in the plugin settings. */
    boolean heartbeat(String watchedDeviceId) throws Exception {
        String t = text("POST", "/JellyXR/Companion/Heartbeat?deviceId=" + enc(watchedDeviceId), null);
        return new JSONObject(t).optBoolean("Enabled", true);
    }

    /** Tells Jellycap which playback device this app covers. False = overlay off in Jellycap, or Jellycap missing / older than 1.1. */
    boolean jellycapHeartbeat(String watchedDeviceId) throws Exception {
        try {
            String t = text("POST", "/Jellycap/Companion/Heartbeat?deviceId=" + enc(watchedDeviceId), null);
            return new JSONObject(t).optBoolean("Enabled", false);
        } catch (HttpError e) {
            if (e.code == 404) return false;
            throw e;
        }
    }

    /** Jellycap recap cards for the item playing on the watched device. Null = none (204). Status "preparing" = ask again. */
    JSONObject recapOverlay(String itemId, String watchedDeviceId) throws Exception {
        String t = text("GET", "/Jellycap/Overlay/" + enc(itemId) + "?deviceId=" + enc(watchedDeviceId), null);
        return t.trim().isEmpty() ? null : new JSONObject(t);
    }

    /** Text recap: asks Jellycap to pause the episode while the cards show. */
    void recapHold(String itemId, String watchedDeviceId) throws Exception {
        call("POST", "/Jellycap/Overlay/" + enc(itemId) + "/Hold?deviceId=" + enc(watchedDeviceId), null);
    }

    /** Text recap over. resume = Jellycap seeks the episode back to its start position and plays it. */
    void recapRelease(String itemId, String watchedDeviceId, boolean resume) throws Exception {
        call("POST", "/Jellycap/Overlay/" + enc(itemId) + "/Release?deviceId=" + enc(watchedDeviceId) + "&resume=" + resume, null);
    }

    Bitmap personImage(String personId, int heightPx) {
        try {
            byte[] b = call("GET", "/Items/" + enc(personId) + "/Images/Primary?fillHeight=" + heightPx
                + "&fillWidth=" + heightPx + "&quality=90", null);
            return BitmapFactory.decodeByteArray(b, 0, b.length);
        } catch (Exception e) {
            android.util.Log.w("JellyXR", "portrait " + personId + ": " + e);
            return null;
        }
    }

    /** Jellyfin's LAN discovery (UDP 7359). Returns the first server's address, or null. */
    static String discover() {
        DatagramSocket s = null;
        try {
            s = new DatagramSocket();
            s.setBroadcast(true);
            s.setSoTimeout(2500);
            byte[] msg = "who is JellyfinServer?".getBytes(StandardCharsets.UTF_8);
            s.send(new DatagramPacket(msg, msg.length, InetAddress.getByName("255.255.255.255"), 7359));
            byte[] buf = new byte[4096];
            DatagramPacket p = new DatagramPacket(buf, buf.length);
            s.receive(p);
            JSONObject o = new JSONObject(new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8));
            String addr = o.optString("Address", "");
            return addr.isEmpty() ? null : addr;
        } catch (SocketTimeoutException e) {
            return null;
        } catch (Exception e) {
            return null;
        } finally {
            if (s != null) s.close();
        }
    }
}
