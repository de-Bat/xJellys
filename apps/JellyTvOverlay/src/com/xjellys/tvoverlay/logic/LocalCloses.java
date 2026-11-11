package com.xjellys.tvoverlay.logic;

import java.util.HashMap;
import java.util.Iterator;
import java.util.Map;
import java.util.Set;

/**
 * Layout ids this app closed by itself (back, ttl, finished), so a lost {@code closed} event cannot bring the
 * window back: while an id is remembered, a snapshot (reset + shows) does not show it again. An id is forgotten
 * after {@link #SKIP_MS}, when a snapshot no longer lists it, when the host closes it, or when a show outside a
 * snapshot arrives for it (a new show by the plugin). Also the retry policy for {@code closed} events. Main thread
 * only (not thread-safe).
 */
public final class LocalCloses {
    public static final long SKIP_MS = 60000;
    private static final long[] RETRY_MS = {1000, 2000, 4000};

    private final Map<String, Long> closedAt = new HashMap<String, Long>();

    /** The app closed id itself at now (elapsed ms). */
    public void closed(String id, long now) {
        if (id != null) closedAt.put(id, now);
    }

    /** The host closed id (close op): nothing to protect against any more. */
    public void hostClosed(String id) {
        if (id != null) closedAt.remove(id);
    }

    /** A snapshot starts listing listed: forget ids it no longer lists and ids older than SKIP_MS. */
    public void onReset(Set<String> listed, long now) {
        for (Iterator<Map.Entry<String, Long>> it = closedAt.entrySet().iterator(); it.hasNext();) {
            Map.Entry<String, Long> e = it.next();
            if (listed == null || !listed.contains(e.getKey()) || now - e.getValue() >= SKIP_MS) it.remove();
        }
    }

    /** Whether a show of id may be applied. inSnapshot: the show belongs to a reset snapshot. */
    public boolean allowShow(String id, boolean inSnapshot, long now) {
        if (id == null) return true;
        Long at = closedAt.get(id);
        if (at == null) return true;
        if (!inSnapshot || now - at >= SKIP_MS) {
            closedAt.remove(id);
            return true;
        }
        return false;
    }

    public boolean has(String id) {
        return closedAt.containsKey(id);
    }

    /** Delay before retry number attempt (0-based) of a failed {@code closed} event; -1 = give up. */
    public static long retryDelayMs(int attempt) {
        return attempt < 0 || attempt >= RETRY_MS.length ? -1 : RETRY_MS[attempt];
    }

    /** Whether a failed event is worth retrying: network error (code 0) or a server error (5xx). */
    public static boolean retryable(int httpCode) {
        return httpCode == 0 || httpCode >= 500;
    }
}
