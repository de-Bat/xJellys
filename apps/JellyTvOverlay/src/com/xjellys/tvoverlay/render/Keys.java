package com.xjellys.tvoverlay.render;

import android.view.KeyEvent;

/** Remote key codes to layout key names. */
final class Keys {
    private Keys() { }

    /** Layout keys: D-pad, ok (centre/Enter), play (Play/Pause, Play), back. Null for anything else. */
    static String name(int code) {
        switch (code) {
            case KeyEvent.KEYCODE_DPAD_LEFT: return "left";
            case KeyEvent.KEYCODE_DPAD_RIGHT: return "right";
            case KeyEvent.KEYCODE_DPAD_UP: return "up";
            case KeyEvent.KEYCODE_DPAD_DOWN: return "down";
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER: return "ok";
            case KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE:
            case KeyEvent.KEYCODE_MEDIA_PLAY: return "play";
            case KeyEvent.KEYCODE_BACK: return "back";
            default: return null;
        }
    }

    /** Media keys with no layout key of their own: pause toggles like play, FF/next seek right, REW/previous left. */
    static String mediaAlias(int code) {
        switch (code) {
            case KeyEvent.KEYCODE_MEDIA_PAUSE: return "play";
            case KeyEvent.KEYCODE_MEDIA_FAST_FORWARD:
            case KeyEvent.KEYCODE_MEDIA_NEXT: return "right";
            case KeyEvent.KEYCODE_MEDIA_REWIND:
            case KeyEvent.KEYCODE_MEDIA_PREVIOUS: return "left";
            case KeyEvent.KEYCODE_MEDIA_STOP: return "stop";
            default: return null;
        }
    }
}
