package com.xjellys.tvoverlay.render;

import com.xjellys.tvoverlay.R;

/** The bundled icon set (layout v1 "icon" names) as vector drawables. Unknown names give 0 (no icon). */
final class Icons {
    private Icons() { }

    static int res(String name) {
        if (name == null) return 0;
        switch (name) {
            case "play": return R.drawable.ic_play;
            case "pause": return R.drawable.ic_pause;
            case "info": return R.drawable.ic_info;
            case "back": return R.drawable.ic_back;
            case "check": return R.drawable.ic_check;
            case "clock": return R.drawable.ic_clock;
            case "person": return R.drawable.ic_person;
            case "subtitles": return R.drawable.ic_subtitles;
            case "left": return R.drawable.ic_left;
            case "right": return R.drawable.ic_right;
            case "up": return R.drawable.ic_up;
            case "down": return R.drawable.ic_down;
            case "close": return R.drawable.ic_close;
            default: return 0;
        }
    }
}
