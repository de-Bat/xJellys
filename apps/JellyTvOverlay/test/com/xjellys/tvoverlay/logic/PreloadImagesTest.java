package com.xjellys.tvoverlay.logic;

import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;

import org.json.JSONObject;
import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.Layout;

public class PreloadImagesTest {
    private static String img(String src) {
        return "{\"type\":\"image\",\"src\":\"" + src + "\"}";
    }

    @Test
    public void firstSrcsInDocumentOrder_dedupedAndCapped() throws Exception {
        StringBuilder posters = new StringBuilder();
        for (int i = 0; i < 12; i++) posters.append(',').append(img("/p" + i));
        Layout l = Layout.parse(new JSONObject("{\"v\":1,\"id\":\"d\",\"root\":{\"type\":\"stack\",\"children\":["
            + "{\"type\":\"column\",\"children\":[" + img("/portrait") + "," + img("") + ",{\"type\":\"text\",\"text\":\"/x\"}]},"
            + "{\"type\":\"list\",\"children\":[" + img("/portrait") + posters + "]}]}}"));

        assertEquals(Arrays.asList("/portrait", "/p0", "/p1", "/p2", "/p3", "/p4", "/p5", "/p6"), PreloadImages.select(l.root, 8));
        assertEquals(Arrays.asList("/portrait"), PreloadImages.select(l.root, 1));
        assertTrue(PreloadImages.select(l.root, 0).isEmpty());
        assertTrue(PreloadImages.select(null, 8).isEmpty());
    }
}
