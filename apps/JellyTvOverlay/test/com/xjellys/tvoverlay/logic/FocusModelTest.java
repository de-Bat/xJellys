package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.util.HashMap;
import java.util.Map;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.Layout;
import com.xjellys.tvoverlay.model.Node;

public class FocusModelTest {
    private static Layout lay(String focus, String keys, String children) throws Exception {
        return Layout.parse(new JSONObject("{\"v\":1,\"id\":\"l\",\"focus\":\"" + focus + "\","
                + (keys == null ? "" : "\"keys\":" + keys + ",")
                + "\"root\":{\"type\":\"column\",\"children\":" + children + "}}"));
    }

    private static String btn(String id, String action) {
        return "{\"type\":\"button\",\"id\":\"" + id + "\",\"action\":\"" + action + "\"}";
    }

    private static Map<String, float[]> rects(Object... kv) {
        Map<String, float[]> m = new HashMap<String, float[]>();
        for (int i = 0; i < kv.length; i += 2) m.put((String) kv[i], (float[]) kv[i + 1]);
        return m;
    }

    private static float[] r(float l, float t, float rr, float b) {
        return new float[] {l, t, rr, b};
    }

    @Test
    public void initialFocus_firstFocusable() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10), "b", r(0, 20, 10, 30)));
        assertEquals("a", f.focused());
    }

    @Test
    public void focus_skipsNodesWithoutRect() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("b", r(0, 20, 10, 30)));
        assertEquals("b", f.focused());
    }

    @Test
    public void down_movesToNearestBelow() throws Exception {
        Layout l = lay("interactive", null,
                "[" + btn("a", "x") + "," + btn("far", "y") + "," + btn("near", "z") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 100, 50), "far", r(0, 400, 100, 450),
                "near", r(0, 100, 100, 150)));
        FocusModel.Result res = f.onKey("down");
        assertEquals(FocusModel.Kind.MOVED, res.kind);
        assertEquals("near", f.focused());
        assertEquals("near", res.elementId);
    }

    @Test
    public void down_prefersOverlappingColumn() throws Exception {
        Layout l = lay("interactive", null,
                "[" + btn("a", "x") + "," + btn("side", "y") + "," + btn("under", "z") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 100, 50), "side", r(200, 60, 300, 110),
                "under", r(0, 300, 100, 350)));
        f.onKey("down");
        assertEquals("under", f.focused());
    }

    @Test
    public void noCandidate_inDirection_isNone() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 100, 100, 150), "b", r(0, 0, 100, 50)));
        assertEquals(FocusModel.Kind.NONE, f.onKey("down").kind);
        assertEquals("a", f.focused());
    }

    @Test
    public void rtlRow_rightMovesToVisualRight() throws Exception {
        // document order a,b but b is visually left of a
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(200, 0, 300, 50), "b", r(0, 0, 100, 50)));
        assertEquals("a", f.focused());
        assertEquals(FocusModel.Kind.NONE, f.onKey("right").kind);
        assertEquals(FocusModel.Kind.MOVED, f.onKey("left").kind);
        assertEquals("b", f.focused());
        assertEquals(FocusModel.Kind.MOVED, f.onKey("right").kind);
        assertEquals("a", f.focused());
    }

    @Test
    public void binding_overridesMove() throws Exception {
        Layout l = lay("interactive", "{\"down\":{\"op\":\"action\",\"action\":\"dn\"}}",
                "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10), "b", r(0, 50, 10, 60)));
        FocusModel.Result res = f.onKey("down");
        assertEquals(FocusModel.Kind.ACTION, res.kind);
        assertEquals("dn", res.action);
        assertEquals("a", f.focused());
    }

    @Test
    public void binding_step_returnsTargetAndBy() throws Exception {
        Layout l = Layout.parse(new JSONObject("{\"v\":1,\"id\":\"l\",\"focus\":\"interactive\","
                + "\"keys\":{\"left\":{\"op\":\"step\",\"target\":\"v\",\"by\":-250}},"
                + "\"root\":{\"type\":\"column\",\"children\":[" + btn("a", "x")
                + ",{\"type\":\"value\",\"id\":\"v\"}]}}"));
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        FocusModel.Result res = f.onKey("left");
        assertEquals(FocusModel.Kind.STEPPED, res.kind);
        assertEquals("v", res.target);
        assertEquals(-250.0, res.by, 0.0);
    }

    @Test
    public void ok_onButton_action() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "go") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        FocusModel.Result res = f.onKey("ok");
        assertEquals(FocusModel.Kind.ACTION, res.kind);
        assertEquals("go", res.action);
        assertEquals("a", res.elementId);
    }

    @Test
    public void ok_withoutAction_isNone() throws Exception {
        Layout l = lay("interactive", null, "[{\"type\":\"text\",\"id\":\"t\",\"focusable\":true}]");
        FocusModel f = new FocusModel(l, rects("t", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.NONE, f.onKey("ok").kind);
    }

    @Test
    public void disabledButton_skipped() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x")
                + ",{\"type\":\"button\",\"id\":\"off\",\"action\":\"y\",\"enabled\":false}," + btn("c", "z") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10), "off", r(0, 20, 10, 30), "c", r(0, 40, 10, 50)));
        f.onKey("down");
        assertEquals("c", f.focused());
    }

    @Test
    public void scrollText_noMove_isNone() throws Exception {
        Layout l = lay("interactive", null, "[{\"type\":\"text\",\"id\":\"bio\",\"focusable\":true,\"scroll\":true}]");
        FocusModel f = new FocusModel(l, rects("bio", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.NONE, f.onKey("down").kind);
    }

    @Test
    public void passthrough_play_andUnboundLeftRight() throws Exception {
        Layout l = lay("interactive+passthrough", null, "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.PASSTHROUGH, f.onKey("play").kind);
        assertEquals(FocusModel.Kind.PASSTHROUGH, f.onKey("left").kind);
        assertEquals(FocusModel.Kind.PASSTHROUGH, f.onKey("right").kind);
        assertEquals(FocusModel.Kind.NONE, f.onKey("up").kind);
    }

    @Test
    public void interactiveOnly_play_isNone() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.NONE, f.onKey("play").kind);
    }

    @Test
    public void back_returnsBack() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.BACK, f.onKey("back").kind);
    }

    @Test
    public void back_bound_stillCloses() throws Exception {
        // R15: BACK always gives the remote back; a back binding only adds an action
        Layout l = lay("interactive", "{\"back\":{\"op\":\"action\",\"action\":\"bye\"}}", "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        FocusModel.Result res = f.onKey("back");
        assertEquals(FocusModel.Kind.BACK, res.kind);
        assertEquals("bye", res.action);
    }

    @Test
    public void back_boundStep_stillClosesWithoutAction() throws Exception {
        Layout l = lay("interactive", "{\"back\":{\"op\":\"step\",\"target\":\"v\",\"by\":1}}", "[" + btn("a", "x")
                + ",{\"type\":\"value\",\"id\":\"v\",\"value\":0}]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        FocusModel.Result res = f.onKey("back");
        assertEquals(FocusModel.Kind.BACK, res.kind);
        assertNull(res.action);
    }

    @Test
    public void back_unbound_hasNoAction() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        assertNull(f.onKey("back").action);
    }

    @Test
    public void unknownKey_isNone() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10)));
        assertEquals(FocusModel.Kind.NONE, f.onKey("menu").kind);
        assertEquals(FocusModel.Kind.NONE, f.onKey(null).kind);
    }

    @Test
    public void setFocused_keepsKnownIdOnly() throws Exception {
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "]");
        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10), "b", r(0, 20, 10, 30)));
        assertTrue(f.setFocused("b"));
        assertEquals("b", f.focused());
        assertFalse(f.setFocused("nope"));
        assertFalse(f.setFocused(null));
        assertEquals("b", f.focused());
    }

    @Test
    public void showFor_visibleOnlyWhenFocused() throws Exception {
        String desc = "{\"type\":\"column\",\"id\":\"desc:a\",\"showFor\":\"a\",\"children\":["
                + "{\"type\":\"button\",\"id\":\"inside\",\"action\":\"z\"}]}";
        Layout l = lay("interactive", null, "[" + btn("a", "x") + "," + btn("b", "y") + "," + desc
                + ",{\"type\":\"text\",\"id\":\"plain\",\"text\":\"t\"}]");
        Node d = l.root.children.get(2);
        Node plain = l.root.children.get(3);
        assertEquals("a", d.showFor());
        assertNull(plain.showFor());

        FocusModel f = new FocusModel(l, rects("a", r(0, 0, 10, 10), "b", r(0, 20, 10, 30), "inside", r(0, 40, 10, 50)));
        assertEquals("a", f.focused());
        assertTrue(f.visible(d));
        assertTrue(f.visible(plain));

        f.onKey("down");
        assertEquals("b", f.focused());
        assertFalse(f.visible(d));
        assertTrue(f.visible(plain));

        // nodes inside a showFor node never take focus (they may be hidden)
        assertEquals(FocusModel.Kind.NONE, f.onKey("down").kind);
        assertEquals("b", f.focused());
        assertFalse(f.setFocused("inside"));

        assertTrue(FocusModel.visibleFor(d, "a"));
        assertFalse(FocusModel.visibleFor(d, null));
        assertTrue(FocusModel.visibleFor(plain, null));
    }
    private static String scrollText(String id) {
        return "{\"type\":\"text\",\"id\":\"" + id + "\",\"text\":\"long\",\"scroll\":true,\"focusable\":true}";
    }

    /** Probe that says the text can still scroll in dir while more[0] is true. */
    private static FocusModel.ScrollProbe probe(final String dir, final boolean[] more) {
        return new FocusModel.ScrollProbe() {
            @Override public boolean canScroll(String id, String d) {
                return "bio".equals(id) && dir.equals(d) && more[0];
            }
        };
    }

    @Test
    public void scrollText_consumesDownUntilEnd_thenMoves() throws Exception {
        Layout l = lay("interactive", null, "[" + scrollText("bio") + "," + btn("a", "x") + "]");
        boolean[] more = {true};
        FocusModel f = new FocusModel(l, rects("bio", r(0, 0, 100, 50), "a", r(0, 100, 100, 150)), probe("down", more));
        assertEquals("bio", f.focused());

        assertEquals(FocusModel.Kind.NONE, f.onKey("down").kind); // the window scrolls the bio
        assertEquals("bio", f.focused());
        more[0] = false; // at the end
        FocusModel.Result res = f.onKey("down");
        assertEquals(FocusModel.Kind.MOVED, res.kind);
        assertEquals("a", f.focused());
    }

    @Test
    public void scrollText_consumesUpUntilTop_thenMoves() throws Exception {
        // bio first in document order (initial focus) but below the button
        Layout l = lay("interactive", null, "[" + scrollText("bio") + "," + btn("a", "x") + "]");
        boolean[] more = {true};
        FocusModel f = new FocusModel(l, rects("bio", r(0, 100, 100, 150), "a", r(0, 0, 100, 50)), probe("up", more));
        assertEquals("bio", f.focused());

        assertEquals(FocusModel.Kind.NONE, f.onKey("up").kind);
        assertEquals("bio", f.focused());
        // down cannot scroll (probe says only up) and nothing is below: still NONE, focus stays
        assertEquals(FocusModel.Kind.NONE, f.onKey("down").kind);
        more[0] = false;
        assertEquals(FocusModel.Kind.MOVED, f.onKey("up").kind);
        assertEquals("a", f.focused());
    }

    @Test
    public void scrollText_withoutProbe_movesFirst() throws Exception {
        Layout l = lay("interactive", null, "[" + scrollText("bio") + "," + btn("a", "x") + "]");
        FocusModel f = new FocusModel(l, rects("bio", r(0, 0, 100, 50), "a", r(0, 100, 100, 150)));
        assertEquals(FocusModel.Kind.MOVED, f.onKey("down").kind);
    }
}
