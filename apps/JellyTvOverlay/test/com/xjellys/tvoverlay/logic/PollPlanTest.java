package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.HashSet;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

public class PollPlanTest {
    private static String show(long seq, String id) {
        return "{\"seq\":" + seq + ",\"op\":\"show\",\"layoutId\":\"" + id + "\",\"layout\":{\"v\":1,\"id\":\"" + id
            + "\",\"root\":{\"type\":\"text\",\"text\":\"hi\"}}}";
    }

    private static PollPlan plan(String json, long since) {
        return PollPlan.of(new JSONObject(json), since);
    }

    @Test
    public void emptyResponse_keepsSince_noSteps() {
        PollPlan p = plan("{\"seq\":4,\"ops\":[]}", 4);
        assertEquals(4, p.seq);
        assertTrue(p.steps.isEmpty());
    }

    @Test
    public void missingSeq_keepsSince() {
        PollPlan p = plan("{\"ops\":[]}", 9);
        assertEquals(9, p.seq);
    }

    @Test
    public void showAndClose_inOrder() {
        PollPlan p = plan("{\"seq\":6,\"ops\":[" + show(5, "a/x") + ",{\"seq\":6,\"op\":\"close\",\"layoutId\":\"a/y\"}]}", 4);
        assertEquals(6, p.seq);
        assertEquals(2, p.steps.size());
        assertEquals(PollPlan.Kind.SHOW, p.steps.get(0).kind);
        assertEquals("a/x", p.steps.get(0).id);
        assertEquals("a/x", p.steps.get(0).layout.getString("id"));
        assertEquals(PollPlan.Kind.CLOSE, p.steps.get(1).kind);
        assertEquals("a/y", p.steps.get(1).id);
        assertNull(p.steps.get(1).layout);
    }

    @Test
    public void reset_keepsTheSnapshotShows() {
        PollPlan p = plan("{\"seq\":7,\"ops\":[{\"seq\":7,\"op\":\"reset\",\"layoutId\":\"\"}," + show(7, "a/x") + ","
            + show(7, "b/y") + "]}", 0);
        assertEquals(7, p.seq);
        assertEquals(3, p.steps.size());
        PollPlan.Step r = p.steps.get(0);
        assertEquals(PollPlan.Kind.RESET, r.kind);
        assertEquals(new HashSet<String>(Arrays.asList("a/x", "b/y")), r.keep);
        assertEquals(PollPlan.Kind.SHOW, p.steps.get(1).kind);
        assertEquals(PollPlan.Kind.SHOW, p.steps.get(2).kind);
    }

    @Test
    public void reset_withNoShows_closesEverything() {
        PollPlan p = plan("{\"seq\":3,\"ops\":[{\"seq\":3,\"op\":\"reset\",\"layoutId\":\"\"}]}", 99);
        assertEquals(3, p.seq);
        assertTrue(p.steps.get(0).keep.isEmpty());
    }

    @Test
    public void reset_keep_stopsAtLaterSeq() {
        // a show after the snapshot (higher seq) is not part of it
        PollPlan p = plan("{\"seq\":8,\"ops\":[{\"seq\":7,\"op\":\"reset\",\"layoutId\":\"\"}," + show(7, "a/x") + ","
            + show(8, "a/z") + "]}", 0);
        assertEquals(new HashSet<String>(Arrays.asList("a/x")), p.steps.get(0).keep);
        assertEquals(3, p.steps.size());
    }

    @Test
    public void showId_comesFromTheLayout() {
        PollPlan p = plan("{\"seq\":2,\"ops\":[{\"seq\":2,\"op\":\"show\",\"layoutId\":\"other\",\"layout\":{\"v\":1,\"id\":\"a/x\",\"root\":{}}}]}", 0);
        assertEquals("a/x", p.steps.get(0).id);
    }

    @Test
    public void malformedOps_skipped() {
        PollPlan p = plan("{\"seq\":5,\"ops\":[1,null,{\"op\":\"show\",\"layoutId\":\"a/x\"},{\"op\":\"close\"},"
            + "{\"op\":\"dance\",\"layoutId\":\"a/x\"}," + show(5, "a/ok") + "]}", 0);
        assertEquals(1, p.steps.size());
        assertEquals("a/ok", p.steps.get(0).id);
    }

    @Test
    public void noOpsArray_noSteps() {
        PollPlan p = plan("{\"seq\":5}", 1);
        assertEquals(5, p.seq);
        assertTrue(p.steps.isEmpty());
    }
}
