package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.Layout;

public class ValueStateTest {
    private static ValueState vs(double initial) throws Exception {
        return new ValueState(Layout.parse(new JSONObject("{\"v\":1,\"id\":\"l\",\"root\":{\"type\":\"column\",\"children\":["
                + "{\"type\":\"value\",\"id\":\"off\",\"value\":" + initial
                + ",\"scale\":1000,\"decimals\":2,\"suffix\":\" s\",\"signed\":true,\"min\":-600000,\"max\":600000},"
                + "{\"type\":\"value\",\"id\":\"plain\",\"value\":3}]}}")));
    }

    @Test
    public void stepUp_displaysSignedSeconds() throws Exception {
        ValueState s = vs(0);
        assertTrue(s.step("off", 250));
        assertEquals("+0.25 s", s.display("off"));
    }

    @Test
    public void stepDown_displaysNegative() throws Exception {
        ValueState s = vs(0);
        s.step("off", -1000);
        assertEquals("-1.00 s", s.display("off"));
        assertEquals(-1000.0, s.get("off"), 0.0);
    }

    @Test
    public void clampsAtMax_andReportsNoChange() throws Exception {
        ValueState s = vs(599900);
        assertTrue(s.step("off", 250));
        assertEquals(600000.0, s.get("off"), 0.0);
        assertFalse(s.step("off", 250));
    }

    @Test
    public void initialValue_clamped() throws Exception {
        assertEquals(600000.0, vs(9e9).get("off"), 0.0);
    }

    @Test
    public void plain_andUnknownId() throws Exception {
        ValueState s = vs(0);
        assertEquals("3", s.display("plain"));
        assertFalse(s.step("nope", 1));
        assertEquals(0.0, s.get("nope"), 0.0);
        assertEquals("", s.display("nope"));
    }

    @Test
    public void zero_showsPlus_andNoNegativeZero() throws Exception {
        ValueState s = vs(0);
        assertEquals("+0.00 s", s.display("off"));
        s.step("off", -1);
        assertEquals("+0.00 s", s.display("off"));
    }

    @Test
    public void snapshot_hasAllValues() throws Exception {
        ValueState s = vs(0);
        s.step("off", 500);
        JSONObject o = s.snapshot();
        assertEquals(500.0, o.getDouble("off"), 0.0);
        assertEquals(3.0, o.getDouble("plain"), 0.0);
    }
}
