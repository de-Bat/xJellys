package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.Layout;

public class PreparedShowTest {
    private static final String CHILD = "{\"v\":1,\"id\":\"a/detail\",\"root\":{\"type\":\"image\",\"src\":\"/Items/1/Images/Primary\"}}";
    private static final String PARENT = "{\"v\":1,\"id\":\"a/cast\",\"root\":{\"type\":\"text\",\"text\":\"x\"},\"preload\":[" + CHILD + ",{\"bad\":1}]}";

    @Test
    public void parsesAndCanonicalizes_preloadsByIdToo() {
        PreparedShow p = PreparedShow.of(new JSONObject(PARENT));
        assertNotNull(p.layout);
        assertEquals("a/cast", p.layout.id);
        assertEquals(Canon.of(new JSONObject(PARENT)), p.canon);
        assertEquals(Canon.of(new JSONObject(CHILD)), p.preloadCanons.get("a/detail"));
        assertEquals(1, p.preloadCanons.size());
    }

    @Test
    public void invalidLayout_hasErrorNoLayout() {
        PreparedShow p = PreparedShow.of(new JSONObject("{\"v\":2,\"id\":\"x\",\"root\":{}}"));
        assertNull(p.layout);
        assertNotNull(p.error);
    }

    @Test
    public void preparedPath_hitsPreloadCache() {
        PreloadCache c = new PreloadCache(8);
        PreparedShow parent = PreparedShow.of(new JSONObject(PARENT));
        c.addFrom(parent, parent.layout);
        Layout cached = parent.layout.preload.get(0);

        assertSame(cached, c.take(PreparedShow.of(new JSONObject(CHILD))));
        assertNull(c.take(PreparedShow.of(new JSONObject(CHILD.replace("Primary", "Backdrop")))));
    }

    @Test
    public void pollPlan_preparesShowsOnTheCallingThread() {
        PollPlan plan = PollPlan.of(new JSONObject("{\"seq\":3,\"ops\":[{\"seq\":3,\"op\":\"show\",\"layoutId\":\"a/cast\",\"layout\":"
            + PARENT + "},{\"seq\":3,\"op\":\"close\",\"layoutId\":\"a/x\"}]}"), 0);
        assertNotNull(plan.steps.get(0).prepared);
        assertEquals("a/cast", plan.steps.get(0).prepared.layout.id);
        assertNull(plan.steps.get(1).prepared);
    }
}
