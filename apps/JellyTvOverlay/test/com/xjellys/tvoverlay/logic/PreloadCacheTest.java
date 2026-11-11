package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.Layout;

public class PreloadCacheTest {
    private static final String CHILD = "{\"v\":1,\"id\":\"a/detail\",\"root\":{\"type\":\"image\",\"src\":\"/Items/1/Images/Primary\"}}";
    private static final String CHILD_REORDERED = "{\"root\":{\"src\":\"/Items/1/Images/Primary\",\"type\":\"image\"},\"id\":\"a/detail\",\"v\":1}";

    private static JSONObject parent(String child) {
        return new JSONObject("{\"v\":1,\"id\":\"a/cast\",\"root\":{\"type\":\"text\",\"text\":\"x\"},\"preload\":[" + child + ",{\"bad\":1}]}");
    }

    @Test
    public void showOfPreloadedId_sameJson_usesCachedLayout() throws Exception {
        PreloadCache c = new PreloadCache(8);
        JSONObject p = parent(CHILD);
        Layout pl = Layout.parse(p);
        c.addFrom(p, pl);
        Layout cached = pl.preload.get(0);
        assertSame(cached, c.take(new JSONObject(CHILD)));
        // key order does not matter
        assertSame(cached, c.take(new JSONObject(CHILD_REORDERED)));
    }

    @Test
    public void differentJson_missesCache() throws Exception {
        PreloadCache c = new PreloadCache(8);
        JSONObject p = parent(CHILD);
        c.addFrom(p, Layout.parse(p));
        assertNull(c.take(new JSONObject(CHILD.replace("Primary", "Backdrop"))));
        assertNull(c.take(new JSONObject("{\"v\":1,\"id\":\"a/other\",\"root\":{}}")));
    }

    @Test
    public void evictsOldest() throws Exception {
        PreloadCache c = new PreloadCache(2);
        for (int i = 0; i < 3; i++) {
            String ch = CHILD.replace("a/detail", "a/d" + i);
            JSONObject p = parent(ch);
            c.addFrom(p, Layout.parse(p));
        }
        assertEquals(2, c.size());
        assertNull(c.take(new JSONObject(CHILD.replace("a/detail", "a/d0"))));
        assertNotNull(c.take(new JSONObject(CHILD.replace("a/detail", "a/d2"))));
    }

    @Test
    public void canon_sortsKeysRecursively() {
        assertEquals(Canon.of(new JSONObject("{\"b\":[1,{\"y\":2,\"x\":\"s\"}],\"a\":null}")),
            Canon.of(new JSONObject("{\"a\":null,\"b\":[1,{\"x\":\"s\",\"y\":2}]}")));
        assertNotEquals(Canon.of(new JSONObject("{\"a\":\"1\"}")), Canon.of(new JSONObject("{\"a\":1}")));
    }
}
