package com.xjellys.tvoverlay.logic;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;

/** Reads a response body with a size cap; a larger body fails like a network error (IOException). */
public final class BodyLimit {
    /** Poll responses: 4 MB (the host caps one layout at 1 MB). */
    public static final int POLL_MAX_BYTES = 4 * 1024 * 1024;

    private BodyLimit() {}

    /** All of in, or IOException as soon as it passes maxBytes. Closes in. */
    public static byte[] read(InputStream in, int maxBytes) throws IOException {
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) > 0) {
                if (out.size() + n > maxBytes) throw new IOException("response over " + maxBytes + " bytes");
                out.write(buf, 0, n);
            }
            return out.toByteArray();
        } finally {
            in.close();
        }
    }
}
