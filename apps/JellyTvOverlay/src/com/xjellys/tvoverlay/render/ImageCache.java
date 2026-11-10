package com.xjellys.tvoverlay.render;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Handler;
import android.os.Looper;
import android.util.LruCache;
import android.view.ViewGroup;
import android.widget.ImageView;

import java.io.IOException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;

import javax.net.ssl.HttpsURLConnection;

import com.xjellys.tvoverlay.Api;
import com.xjellys.tvoverlay.model.ImageSource;

/**
 * Decoded bitmaps by src, bounded in bytes (LRU). Fetch rules come from {@link ImageSource}: SERVER through
 * {@link Api} (HTTP gate, token header, no redirects); EXTERNAL with a plain https connection that carries no
 * Authorization header and follows no redirects; REJECT leaves the view empty. Decoding is subsampled to the
 * view's target size. Call from the main thread; network and decoding run on a small pool.
 */
public final class ImageCache {
    private static final int MAX_DOWNLOAD = 8 * 1024 * 1024;
    private static final int MAX_PIXELS = 4 * 1024 * 1024;
    private static final int DEFAULT_TARGET = 640;

    private final Api api;
    private final LruCache<String, Bitmap> cache;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Map<String, List<ImageView>> waiting = new HashMap<String, List<ImageView>>();
    private final ExecutorService pool = Executors.newFixedThreadPool(3, new ThreadFactory() {
        @Override public Thread newThread(Runnable r) {
            Thread t = new Thread(r, "overlay-images");
            t.setDaemon(true);
            return t;
        }
    });

    public ImageCache(Api api, int maxBytes) {
        this.api = api;
        this.cache = new LruCache<String, Bitmap>(Math.max(1, maxBytes)) {
            @Override protected int sizeOf(String k, Bitmap b) {
                return b.getAllocationByteCount();
            }
        };
    }

    /** Fetches and decodes src into the cache without a view (so a later show is instant). */
    public void preload(String src) {
        if (src == null || src.isEmpty() || cache.get(src) != null) return;
        load(src, DEFAULT_TARGET, DEFAULT_TARGET, null);
    }

    /** Shows src in v when it arrives; v stays empty for a rejected or failed src. Target size = v's layout params. */
    public void into(ImageView v, String src) {
        v.setTag(src);
        v.setImageDrawable(null);
        if (src == null || src.isEmpty()) return;
        Bitmap b = cache.get(src);
        if (b != null) {
            v.setImageBitmap(b);
            return;
        }
        ViewGroup.LayoutParams lp = v.getLayoutParams();
        int tw = lp != null && lp.width > 0 ? lp.width : DEFAULT_TARGET;
        int th = lp != null && lp.height > 0 ? lp.height : DEFAULT_TARGET;
        load(src, tw, th, v);
    }

    private void load(final String src, final int tw, final int th, ImageView v) {
        List<ImageView> w = waiting.get(src);
        if (w != null) {
            if (v != null) w.add(v);
            return;
        }
        final ImageSource.Kind kind = ImageSource.classify(src, api == null ? null : api.base());
        if (kind == ImageSource.Kind.REJECT) return;
        w = new ArrayList<ImageView>();
        if (v != null) w.add(v);
        waiting.put(src, w);
        pool.execute(new Runnable() {
            @Override public void run() {
                Bitmap b = null;
                try {
                    byte[] d = kind == ImageSource.Kind.SERVER
                        ? api.image(ImageSource.serverPath(src, api.base()), MAX_DOWNLOAD)
                        : external(src);
                    if (d != null) b = decode(d, tw, th);
                } catch (Exception e) {
                    b = null; // no image; never log the URL (it may carry ids)
                } catch (OutOfMemoryError e) {
                    b = null;
                }
                final Bitmap fb = b;
                main.post(new Runnable() {
                    @Override public void run() { deliver(src, fb); }
                });
            }
        });
    }

    private void deliver(String src, Bitmap b) {
        List<ImageView> views = waiting.remove(src);
        if (b == null) return;
        cache.put(src, b);
        if (views == null) return;
        for (ImageView v : views) {
            if (src.equals(v.getTag())) v.setImageBitmap(b);
        }
    }

    /** Another https origin: no Authorization header, no redirects, no caching of credentials. */
    private static byte[] external(String src) throws IOException {
        URL u = new URL(src);
        if (!"https".equalsIgnoreCase(u.getProtocol())) return null;
        HttpURLConnection c = (HttpURLConnection) u.openConnection();
        if (!(c instanceof HttpsURLConnection)) {
            c.disconnect();
            return null;
        }
        try {
            c.setInstanceFollowRedirects(false);
            c.setConnectTimeout(5000);
            c.setReadTimeout(10000);
            c.setRequestProperty("Accept", "image/*");
            if (c.getResponseCode() != 200) return null;
            return Api.readCapped(c.getInputStream(), MAX_DOWNLOAD);
        } finally {
            c.disconnect();
        }
    }

    /** Decodes with the largest power-of-two subsample that still covers tw x th. */
    static Bitmap decode(byte[] d, int tw, int th) {
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(d, 0, d.length, o);
        if (o.outWidth <= 0 || o.outHeight <= 0) return null;
        int s = 1;
        while (o.outWidth / (s * 2) >= tw && o.outHeight / (s * 2) >= th) s *= 2;
        while ((long) (o.outWidth / s) * (o.outHeight / s) > MAX_PIXELS) s *= 2;
        BitmapFactory.Options d2 = new BitmapFactory.Options();
        d2.inSampleSize = s;
        return BitmapFactory.decodeByteArray(d, 0, d.length, d2);
    }
}
