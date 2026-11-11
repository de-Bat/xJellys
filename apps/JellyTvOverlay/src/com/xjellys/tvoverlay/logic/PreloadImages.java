package com.xjellys.tvoverlay.logic;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

import com.xjellys.tvoverlay.model.Node;

/** Which images of a preloaded layout to fetch ahead: the first {@code max} distinct image srcs in document order. */
public final class PreloadImages {
    private PreloadImages() {
    }

    public static List<String> select(Node root, int max) {
        Set<String> out = new LinkedHashSet<String>();
        if (root != null && max > 0) collect(root, max, out);
        return new ArrayList<String>(out);
    }

    private static void collect(Node n, int max, Set<String> out) {
        if (out.size() >= max) return;
        if ("image".equals(n.type)) {
            String s = n.src();
            if (!s.isEmpty()) out.add(s);
        }
        for (Node c : n.children) {
            if (out.size() >= max) return;
            collect(c, max, out);
        }
    }
}
