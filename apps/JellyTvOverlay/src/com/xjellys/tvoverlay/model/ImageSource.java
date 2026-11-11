package com.xjellys.tvoverlay.model;

import java.util.Locale;

/**
 * Where an image {@code src} may be fetched from (spec "Security").
 * SERVER: a path starting with a single {@code /}, or an absolute URL with exactly the server's origin
 * (scheme, host, port), and only while the server address itself is allowed; fetched with the token.
 * EXTERNAL: any other {@code https} origin; fetched with no token. REJECT: everything else
 * (http to another host, file:, content:, javascript:, data:, userinfo, whitespace, backslashes).
 */
public final class ImageSource {
    public enum Kind { SERVER, EXTERNAL, REJECT }

    private ImageSource() { }

    public static Kind classify(String src, String serverBase) {
        if (src == null || src.isEmpty() || !clean(src)) return Kind.REJECT;
        boolean serverOk = serverBase != null && ServerUrl.allowed(serverBase) && origin(serverBase) != null;
        if (src.charAt(0) == '/') {
            if (src.length() > 1 && src.charAt(1) == '/') return Kind.REJECT; // protocol-relative
            return serverOk ? Kind.SERVER : Kind.REJECT;
        }
        String o = origin(src);
        if (o == null) return Kind.REJECT;
        if (serverOk && o.equals(origin(serverBase))) return Kind.SERVER;
        return o.startsWith("https://") ? Kind.EXTERNAL : Kind.REJECT;
    }

    /**
     * Path (with query) to request relative to the server base for a SERVER src. For an absolute URL the base's
     * own path prefix (a reverse-proxy subpath such as /jellyfin) is removed, since the base already carries it.
     */
    public static String serverPath(String src, String serverBase) {
        if (src.charAt(0) == '/') return src;
        String p = pathPart(src);
        String bp = serverBase == null ? "" : pathPart(serverBase);
        while (bp.endsWith("/")) bp = bp.substring(0, bp.length() - 1);
        if (!bp.isEmpty() && (p.equals(bp) || p.startsWith(bp + "/") || p.startsWith(bp + "?"))) {
            p = p.substring(bp.length());
        }
        return p.isEmpty() ? "/" : (p.charAt(0) == '/' ? p : "/" + p);
    }

    private static String pathPart(String url) {
        int i = url.indexOf("://");
        return i < 0 ? "" : url.substring(authorityEnd(url, i + 3));
    }

    /** No control chars, spaces or backslashes anywhere (URL parsers disagree on them). */
    private static boolean clean(String s) {
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c <= 0x20 || c == 0x7f || c == '\\') return false;
        }
        return true;
    }

    private static int authorityEnd(String s, int from) {
        for (int k = from; k < s.length(); k++) {
            char c = s.charAt(k);
            if (c == '/' || c == '?' || c == '#') return k;
        }
        return s.length();
    }

    /** "scheme://host:port" lower-cased with the default port filled in; null unless http(s) and well formed. */
    static String origin(String url) {
        if (url == null) return null;
        int i = url.indexOf("://");
        if (i <= 0) return null;
        String scheme = url.substring(0, i).toLowerCase(Locale.ROOT);
        int def;
        if (scheme.equals("https")) def = 443;
        else if (scheme.equals("http")) def = 80;
        else return null;
        String a = url.substring(i + 3, authorityEnd(url, i + 3));
        if (a.isEmpty() || a.indexOf('@') >= 0) return null;
        String host;
        String rest;
        if (a.startsWith("[")) {
            int e = a.indexOf(']');
            if (e < 0) return null;
            host = a.substring(0, e + 1);
            rest = a.substring(e + 1);
        } else {
            int c = a.indexOf(':');
            host = c < 0 ? a : a.substring(0, c);
            rest = c < 0 ? "" : a.substring(c);
        }
        if (host.isEmpty() || host.equals("[]")) return null;
        int port = def;
        if (!rest.isEmpty()) {
            if (rest.charAt(0) != ':') return null;
            String p = rest.substring(1);
            if (p.isEmpty() || p.length() > 5) return null;
            for (int k = 0; k < p.length(); k++) if (p.charAt(k) < '0' || p.charAt(k) > '9') return null;
            port = Integer.parseInt(p);
            if (port < 1 || port > 65535) return null;
        }
        return scheme + "://" + host.toLowerCase(Locale.ROOT) + ":" + port;
    }
}
