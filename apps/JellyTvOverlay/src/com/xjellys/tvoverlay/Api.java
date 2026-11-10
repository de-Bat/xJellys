package com.xjellys.tvoverlay;

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

import com.xjellys.tvoverlay.model.ServerUrl;

/** Minimal Jellyfin REST client. Every call blocks: run it off the main thread. */
final class Api {
    static final String CLIENT = "JellyTvOverlay";

    /** HTTP status other than 2xx. */
    static final class HttpError extends IOException {
        final int code;

        HttpError(int code, String msg) {
            super("HTTP " + code + (msg == null || msg.isEmpty() ? "" : ": " + msg));
            this.code = code;
        }

        /** plain = the server's own sentence, shown to the viewer as is. */
        HttpError(int code, String msg, boolean plain) {
            super(msg);
            this.code = code;
        }
    }

    private final String base;
    private final String deviceName;
    private final String deviceId;
    private final String token;

    Api(String server, String deviceName, String deviceId, String token) {
        this.base = ServerUrl.normalize(server);
        this.deviceName = deviceName;
        this.deviceId = deviceId;
        this.token = token;
    }

    static Api of(Prefs p) {
        return new Api(p.server(), p.deviceName(), p.deviceId(), p.token());
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

    private HttpURLConnection open(String method, String path, int readMs) throws IOException {
        if (!ServerUrl.allowed(base)) throw new IOException("Plain HTTP is only allowed on the local network");
        HttpURLConnection c = (HttpURLConnection) new URL(base + path).openConnection();
        // never follow a redirect: it could carry the Authorization header to another host or to plain http
        c.setInstanceFollowRedirects(false);
        c.setRequestMethod(method);
        c.setConnectTimeout(5000);
        c.setReadTimeout(readMs);
        c.setRequestProperty("Authorization", auth());
        c.setRequestProperty("Accept", "application/json");
        return c;
    }

    private byte[] call(String method, String path, String jsonBody) throws IOException {
        return call(method, path, jsonBody, 10000);
    }

    private byte[] call(String method, String path, String jsonBody, int readMs) throws IOException {
        try {
            return callOnce(method, path, jsonBody, readMs);
        } catch (HttpError e) {
            throw e;
        } catch (IOException e) {
            // a pooled keep-alive connection the server already closed: retry idempotent GETs once on a fresh one.
            // Not the HTTP gate, not timeouts (long polls), not refused connections.
            boolean stale = e instanceof java.io.EOFException
                || (e instanceof java.net.SocketException && !(e instanceof java.net.ConnectException));
            if (!"GET".equals(method) || !stale) throw e;
            return callOnce(method, path, jsonBody, readMs);
        }
    }

    private byte[] callOnce(String method, String path, String jsonBody, int readMs) throws IOException {
        HttpURLConnection c = open(method, path, readMs);
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
                try {
                    // ASP.NET problem details: the readable reason is "detail"
                    String detail = new JSONObject(msg).optString("detail", "");
                    if (!detail.isEmpty()) throw new HttpError(code, detail, true);
                } catch (org.json.JSONException ignored) {
                    // not JSON
                }
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

    /** Long-poll for overlay ops after {@code since}: {"seq":n,"ops":[...]}. The server holds up to 25 s. */
    JSONObject poll(String deviceId, long since) throws Exception {
        String t = new String(call("GET", "/JellyTvOverlay/Poll?deviceId=" + enc(deviceId) + "&since=" + since, null, 35000),
            StandardCharsets.UTF_8);
        return t.trim().isEmpty() ? new JSONObject().put("seq", since).put("ops", new JSONArray()) : new JSONObject(t);
    }

    /** App event (action or closed) for a layout shown on the device. */
    void event(JSONObject body) throws Exception {
        call("POST", "/JellyTvOverlay/Event", body.toString());
    }

    /** Tells the plugin which playback device this app covers: {"enabled":bool,"protocol":1}. */
    JSONObject heartbeat(String deviceId) throws Exception {
        return new JSONObject(text("POST", "/JellyTvOverlay/Heartbeat?deviceId=" + enc(deviceId), null));
    }

    /** Remote control of the session's player: Pause, Unpause, PlayPause or Seek. */
    void playstate(String sessionId, String command) throws Exception {
        call("POST", "/Sessions/" + enc(sessionId) + "/Playing/" + enc(command), null);
    }

    /** Remote control: stop playback on the session. */
    void stop(String sessionId) throws Exception {
        call("POST", "/Sessions/" + enc(sessionId) + "/Playing/Stop", null);
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
            // a reply naming a public http host (or anything unsafe) is discarded
            return addr.isEmpty() || !ServerUrl.allowed(ServerUrl.normalize(addr)) ? null : addr;
        } catch (SocketTimeoutException e) {
            return null;
        } catch (Exception e) {
            return null;
        } finally {
            if (s != null) s.close();
        }
    }
}
