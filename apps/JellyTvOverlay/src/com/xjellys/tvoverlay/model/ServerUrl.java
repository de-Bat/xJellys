package com.xjellys.tvoverlay.model;

/**
 * Server address rules. https is always allowed; plain http only to private / LAN hosts.
 * "Private" is decided from the literal host only (no DNS), so a public name is never private.
 */
public final class ServerUrl {
    private ServerUrl() { }

    /** Bare host becomes https://host (port 8920 when none is given); trailing slashes are stripped. */
    public static String normalize(String input) {
        String s = input == null ? "" : input.trim();
        while (s.endsWith("/")) s = s.substring(0, s.length() - 1);
        if (s.isEmpty()) return s;
        if (s.contains("://")) return s;
        s = "https://" + s;
        if (!hasPort(authority(s))) s += ":8920";
        return s;
    }

    /** True for literal private IPs (RFC 1918, 127/8, 169.254/16, ::1, fc00::/7, fe80::/10), localhost and *.local. */
    public static boolean isPrivate(String host) {
        if (host == null) return false;
        String h = host.trim().toLowerCase(java.util.Locale.ROOT);
        if (h.startsWith("[") && h.endsWith("]")) h = h.substring(1, h.length() - 1);
        if (h.isEmpty()) return false;
        if (h.equals("localhost") || h.endsWith(".local")) return true;
        if (h.indexOf(':') >= 0) return privateV6(h);
        return privateV4(h);
    }

    /** https always; http only when the host is private. Anything else is refused. */
    public static boolean allowed(String url) {
        if (url == null) return false;
        String u = url.trim().toLowerCase(java.util.Locale.ROOT);
        if (u.startsWith("https://")) return hostOf(url) != null;
        if (u.startsWith("http://")) return isPrivate(hostOf(url));
        return false;
    }

    /** http://host:8096 for an https URL whose host is private, else null. */
    public static String httpFallback(String httpsUrl) {
        if (httpsUrl == null || !httpsUrl.toLowerCase(java.util.Locale.ROOT).startsWith("https://")) return null;
        String host = hostOf(httpsUrl);
        if (!isPrivate(host)) return null;
        return "http://" + (host.indexOf(':') >= 0 ? "[" + host + "]" : host) + ":8096";
    }

    /** Host of an absolute URL without brackets or port, or null when it has userinfo or is malformed. */
    public static String hostOf(String url) {
        String a = authority(url);
        if (a.isEmpty() || a.indexOf('@') >= 0) return null;
        if (a.startsWith("[")) {
            int e = a.indexOf(']');
            return e < 0 ? null : a.substring(1, e);
        }
        int c = a.indexOf(':');
        String h = c < 0 ? a : a.substring(0, c);
        return h.isEmpty() ? null : h;
    }

    private static String authority(String url) {
        int i = url.indexOf("://");
        String rest = i < 0 ? url : url.substring(i + 3);
        int end = rest.length();
        for (int k = 0; k < rest.length(); k++) {
            char c = rest.charAt(k);
            if (c == '/' || c == '?' || c == '#') { end = k; break; }
        }
        return rest.substring(0, end);
    }

    private static boolean hasPort(String a) {
        int from = a.startsWith("[") ? a.indexOf(']') : 0;
        return a.indexOf(':', Math.max(from, 0)) >= 0;
    }

    private static boolean privateV4(String h) {
        String[] p = h.split("[.]", -1);
        if (p.length != 4) return false;
        int[] o = new int[4];
        for (int i = 0; i < 4; i++) {
            if (p[i].isEmpty() || p[i].length() > 3) return false;
            for (int k = 0; k < p[i].length(); k++) if (p[i].charAt(k) < '0' || p[i].charAt(k) > '9') return false;
            o[i] = Integer.parseInt(p[i]);
            if (o[i] > 255) return false;
        }
        return o[0] == 10 || o[0] == 127
            || (o[0] == 172 && o[1] >= 16 && o[1] <= 31)
            || (o[0] == 192 && o[1] == 168)
            || (o[0] == 169 && o[1] == 254);
    }

    private static boolean privateV6(String h) {
        int z = h.indexOf('%');
        if (z >= 0) h = h.substring(0, z);
        for (int i = 0; i < h.length(); i++) {
            char c = h.charAt(i);
            if (!(c == ':' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        if (h.equals("::1")) return true;
        int c = h.indexOf(':');
        String first = h.substring(0, c);
        if (first.isEmpty() || first.length() > 4) return false;
        int v = Integer.parseInt(first, 16);
        return (v & 0xfe00) == 0xfc00 || (v & 0xffc0) == 0xfe80;
    }
}
