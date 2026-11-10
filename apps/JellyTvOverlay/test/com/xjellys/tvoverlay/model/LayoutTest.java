package com.xjellys.tvoverlay.model;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONException;
import org.json.JSONObject;
import org.junit.jupiter.api.Test;

public class LayoutTest {
    private static Layout p(String json) throws JSONException {
        return Layout.parse(new JSONObject(json));
    }

    private static void assertRejected(String json) {
        try {
            p(json);
            fail("expected JSONException");
        } catch (JSONException expected) {
            // ok
        }
    }

    @Test
    public void parse_defaults() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\",\"text\":\"hi\"}}");
        assertEquals("a", l.id);
        assertEquals("passive", l.focus);
        assertFalse(l.interactive());
        assertFalse(l.passthrough());
        assertEquals(0f, l.dim, 0f);
        assertEquals(0, l.ttlMs);
        assertEquals(2, l.closeOn.size());
        assertTrue(l.closeOn.contains("stop") && l.closeOn.contains("itemStart"));
        assertTrue(l.suppressTriggers.isEmpty());
        assertTrue(l.keys.isEmpty());
        assertTrue(l.preload.isEmpty());
        assertNull(l.parent);
        Node t = l.root;
        assertEquals("hi", t.text());
        assertEquals(0.03f, t.size(), 1e-6f);
        assertEquals("auto", t.dir());
        assertTrue(Float.isNaN(t.x) && Float.isNaN(t.w));
        assertTrue(t.enabled);
        assertFalse(t.focusable);
        assertNull(t.bg);
    }

    @Test
    public void parse_requiredFieldsAndVersion() {
        assertRejected("{\"v\":2,\"id\":\"a\",\"root\":{\"type\":\"row\"}}");
        assertRejected("{\"id\":\"a\",\"root\":{\"type\":\"row\"}}");
        assertRejected("{\"v\":1,\"root\":{\"type\":\"row\"}}");
        assertRejected("{\"v\":1,\"id\":\"a\"}");
    }

    @Test
    public void parse_focusModes() throws Exception {
        Layout i = p("{\"v\":1,\"id\":\"a\",\"focus\":\"interactive\",\"root\":{\"type\":\"row\"}}");
        assertTrue(i.interactive());
        assertFalse(i.passthrough());
        Layout pt = p("{\"v\":1,\"id\":\"a\",\"focus\":\"interactive+passthrough\",\"root\":{\"type\":\"row\"}}");
        assertTrue(pt.interactive());
        assertTrue(pt.passthrough());
    }

    @Test
    public void parse_unknownType_kept_noThrow() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"column\",\"children\":["
                + "{\"type\":\"hologram\",\"x\":0.1},{\"type\":\"text\",\"text\":\"z\"}]}}");
        assertEquals(2, l.root.children.size());
        assertEquals("hologram", l.root.children.get(0).type);
        assertEquals(0.1f, l.root.children.get(0).x, 1e-6f);
    }

    @Test
    public void parse_unknownField_ignored() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"zzz\":5,\"root\":{\"type\":\"text\",\"qqq\":[1],\"text\":\"t\"}}");
        assertEquals("t", l.root.text());
    }

    @Test
    public void parse_malformedOptionals_fallBack() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"ttlMs\":\"x\",\"dim\":\"y\",\"closeOn\":5,\"keys\":[1],"
                + "\"root\":{\"type\":\"text\",\"size\":\"big\",\"bg\":\"nope\",\"children\":7,\"x\":\"q\"}}");
        assertEquals(0, l.ttlMs);
        assertEquals(0f, l.dim, 0f);
        assertEquals(2, l.closeOn.size());
        assertNull(l.root.bg);
        assertEquals(0.03f, l.root.size(), 1e-6f);
        assertTrue(l.root.children.isEmpty());
        assertTrue(Float.isNaN(l.root.x));
    }

    @Test
    public void parse_keys_step() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"keys\":{\"left\":{\"op\":\"step\",\"target\":\"off\",\"by\":-50},"
                + "\"back\":{\"op\":\"action\",\"action\":\"bye\"},\"bogus\":5},"
                + "\"root\":{\"type\":\"value\",\"id\":\"off\"}}");
        KeyBinding left = l.keys.get("left");
        assertEquals("step", left.op);
        assertEquals("off", left.target);
        assertEquals(-50.0, left.by, 0.0);
        KeyBinding back = l.keys.get("back");
        assertEquals("action", back.op);
        assertEquals("bye", back.action);
        assertNull(l.keys.get("bogus"));
    }

    @Test
    public void keys_invalidBindingsDropped() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"keys\":{"
                + "\"ok\":{\"op\":\"action\"},"
                + "\"left\":{\"op\":\"step\",\"by\":5},"
                + "\"right\":{\"op\":\"step\",\"target\":\"v\",\"by\":1e999},"
                + "\"up\":{\"op\":\"action\",\"action\":\"u\"},"
                + "\"menu\":{\"op\":\"action\",\"action\":\"m\"}},"
                + "\"root\":{\"type\":\"value\",\"id\":\"v\"}}");
        assertNull(l.keys.get("ok"));
        assertNull(l.keys.get("left"));
        assertNull(l.keys.get("right"));
        assertNull(l.keys.get("menu"));
        assertEquals("u", l.keys.get("up").action);
        assertEquals(1, l.keys.size());
    }

    @Test
    public void floats_nonFiniteAfterCast_useDefault() throws Exception {
        Node n = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"row\",\"x\":1e300,\"pad\":1e300,\"size\":1e300}}").root;
        assertTrue(Float.isNaN(n.x));
        assertEquals(0f, n.pad, 0f);
        assertEquals(0.03f, n.size(), 1e-6f);
    }

    @Test
    public void decimals_clamped() throws Exception {
        assertEquals(6, p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"value\",\"decimals\":99}}").root.decimals());
        assertEquals(0, p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"value\",\"decimals\":-3}}").root.decimals());
    }

    @Test
    public void color_rejectsNonHexChars() throws Exception {
        assertNull(Node.parseColor("#+12345"));
        assertNull(Node.parseColor("#-1234567"));
        assertNull(Node.parseColor("#12345g"));
        assertEquals(Integer.valueOf(0xFF112233), Node.parseColor("#112233"));
    }

    @Test
    public void parse_preload_nested() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"row\"},\"preload\":["
                + "{\"v\":1,\"id\":\"b\",\"root\":{\"type\":\"row\"},\"preload\":[{\"v\":1,\"id\":\"c\",\"root\":{\"type\":\"row\"}}]},"
                + "{\"v\":9,\"id\":\"bad\",\"root\":{\"type\":\"row\"}}]}");
        assertEquals(1, l.preload.size());
        assertEquals("b", l.preload.get(0).id);
        assertEquals("c", l.preload.get(0).preload.get(0).id);
    }

    @Test
    public void parse_suppressTriggers_closeOn() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"closeOn\":[\"resume\"],\"suppressTriggers\":[\"pause\",\"doublePause\"],"
                + "\"parent\":\"p\",\"ttlMs\":500,\"dim\":0.7,\"owner\":\"X\",\"root\":{\"type\":\"row\"}}");
        assertEquals(1, l.closeOn.size());
        assertEquals("resume", l.closeOn.get(0));
        assertEquals(2, l.suppressTriggers.size());
        assertEquals("p", l.parent);
        assertEquals("X", l.owner);
        assertEquals(500, l.ttlMs);
        assertEquals(0.7f, l.dim, 1e-6f);
    }

    @Test
    public void button_disabled_notFocusable() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"button\",\"label\":\"Play\",\"icon\":\"play\","
                + "\"focusable\":true,\"enabled\":false,\"action\":\"go\"}}");
        Node b = l.root;
        assertFalse(b.enabled);
        assertFalse(b.focusable);
        assertEquals("Play", b.label());
        assertEquals("play", b.icon());
        assertEquals("go", b.action);
        Layout e = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"button\",\"label\":\"x\"}}");
        assertTrue(e.root.enabled);
        assertTrue(e.root.focusable);
    }

    @Test
    public void bg_argb() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"stack\",\"bg\":\"#CC000000\",\"children\":["
                + "{\"type\":\"row\",\"bg\":\"#112233\"}]}}");
        assertEquals(Integer.valueOf(0xCC000000), l.root.bg);
        assertEquals(Integer.valueOf(0xFF112233), l.root.children.get(0).bg);
    }

    @Test
    public void typedGetters_perType() throws Exception {
        Layout l = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"column\",\"pad\":0.02,\"gap\":0.01,\"radius\":0.03,\"children\":["
                + "{\"type\":\"text\",\"text\":\"t\",\"size\":0.05,\"color\":\"#FFB0B8C4\",\"bold\":true,\"maxLines\":3,\"dir\":\"rtl\",\"scroll\":true},"
                + "{\"type\":\"image\",\"src\":\"/Items/1\",\"fit\":\"contain\",\"round\":true},"
                + "{\"type\":\"list\",\"orientation\":\"horizontal\"},"
                + "{\"type\":\"pages\",\"intervalMs\":3000},"
                + "{\"type\":\"sequence\",\"finishedAction\":\"done\",\"children\":[{\"type\":\"text\",\"durationMs\":1200}]},"
                + "{\"type\":\"value\",\"id\":\"v\",\"value\":5,\"min\":-10,\"max\":10,\"scale\":100,\"decimals\":2,\"suffix\":\"s\",\"signed\":true,\"changedAction\":\"chg\"}]}}");
        Node r = l.root;
        assertEquals(0.02f, r.pad, 1e-6f);
        assertEquals(0.01f, r.gap, 1e-6f);
        assertEquals(0.03f, r.radius, 1e-6f);
        Node t = r.children.get(0);
        assertEquals(0.05f, t.size(), 1e-6f);
        assertEquals(Integer.valueOf(0xFFB0B8C4), t.color());
        assertTrue(t.bold());
        assertEquals(3, t.maxLines());
        assertEquals("rtl", t.dir());
        assertTrue(t.scroll());
        Node im = r.children.get(1);
        assertEquals("/Items/1", im.src());
        assertEquals("contain", im.fit());
        assertTrue(im.round());
        assertEquals("horizontal", r.children.get(2).orientation());
        assertEquals(3000, r.children.get(3).intervalMs());
        Node s = r.children.get(4);
        assertEquals("done", s.finishedAction());
        assertEquals(1200, s.children.get(0).durationMs());
        Node v = r.children.get(5);
        assertEquals(5.0, v.value(), 0.0);
        assertEquals(-10.0, v.min(), 0.0);
        assertEquals(10.0, v.max(), 0.0);
        assertEquals(100.0, v.scale(), 0.0);
        assertEquals(2, v.decimals());
        assertEquals("s", v.suffix());
        assertTrue(v.signed());
        assertEquals("chg", v.changedAction());
        Node d = p("{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"image\"}}").root;
        assertEquals("cover", d.fit());
        assertFalse(d.round());
        assertEquals("vertical", d.orientation());
        assertEquals(0, d.maxLines());
        assertEquals(1.0, d.scale(), 0.0);
        assertEquals(0, d.decimals());
        assertEquals("", d.suffix());
    }
}
