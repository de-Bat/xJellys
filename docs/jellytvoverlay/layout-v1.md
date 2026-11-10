# JellyTvOverlay layout format v1

A layout is a JSON document that a Jellyfin plugin sends to the JellyTvOverlay host. The host validates it and delivers it to the JellyTvOverlay app on the TV, which renders it. The app holds no plugin knowledge: everything it shows comes from the layout.

Rules that apply everywhere:

- Every layout carries `"v": 1`. A different version is rejected.
- The app ignores unknown node types and unknown fields, so layouts can grow without breaking older apps. The host keeps them too.
- Key presses that move focus, scroll or change a value are handled locally in the app. They never wait for the network.
- Text supports RTL (Hebrew). `dir: "auto"` is resolved per text node from its content.
- `dim` is a flat translucent black layer. There is no blur.
- Only one layout holds the remote per device at a time.

## Layout

| Field | Type | Default | Meaning |
|---|---|---|---|
| `v` | number | required | Must be `1`. |
| `id` | string | required | Layout id. The host namespaces it as `<owner>/<id>` unless it already starts with `<owner>/`. |
| `owner` | string | set by the host | The plugin that sent the layout. Any value you send is overwritten. |
| `focus` | string | `passive` | `passive` (no remote), `interactive` (takes the remote), or `interactive+passthrough` (takes the remote but passes unbound keys on to the player). |
| `ttlMs` | number | `0` | Auto-close after this many ms. `0` = until closed. |
| `dim` | number 0 to 1 | none | Opacity of the flat dim layer behind the layout. |
| `parent` | string | none | Id of a parent layout. Namespaced like `id`. Closing this layout returns to the parent. |
| `closeOn` | array | `["stop","itemStart"]` | Triggers that close the layout. Subset of `itemStart`, `pause`, `resume`, `doublePause`, `seek`, `stop`. |
| `suppressTriggers` | array | `[]` | Same trigger names as `closeOn`. While this layout is shown, the host delivers these triggers only to the layout's owner. |
| `keys` | object | none | Map of key to binding. See Keys. |
| `preload` | array | none | Layouts to cache on the device without showing them. Each is validated with the same owner. Used so a later `Show` is instant: a `Show` whose JSON equals a preloaded entry (key order aside) uses the cached copy. The app also fetches the first 8 images (document order) of each entry ahead of time, after the images of shown windows; the fetches still queued are dropped when the window that carried the preload closes. |
| `root` | node | required | The root node. |

### Keys

Keys: `left` `right` `up` `down` `ok` `play` `back`.

A binding is one of:

- `{"op":"action","action":"<name>"}`: report the action to the owner plugin.
- `{"op":"step","target":"<value node id>","by":<number>}`: add `by` to the `value` node with that id, locally. `target` must name a `value` node inside this layout's `root`, otherwise the layout is rejected.

### Keys on the TV remote

The app maps remote keys as follows: D-pad to `left` `right` `up` `down`, centre and Enter to `ok`, Play/Pause and Play to `play`, and Back to `back`. Back acts on key release.

In an interactive layout, a key is resolved in this order:

1. A binding in `keys` wins.
2. Up/down on a focused `scroll` text scroll it while it can still scroll that way. At its top or end, focus moves on as in 3.
3. Arrows move focus to the nearest focusable node in that direction.
4. Left/right with no possible move:
   - In `interactive+passthrough`, they go to the player (seek). Pages never take them.
   - Only in plain `interactive` layouts do they flip the visible `pages` container.

In `interactive+passthrough` layouts these media keys also go to the player:

| Key | Sent as |
|---|---|
| Pause | `play` (toggle) |
| Fast forward, Next | `right` |
| Rewind, Previous | `left` |
| Stop | `stop` |

In plain `interactive` layouts these media keys are consumed.

## Units and defaults

These are the app's rendering rules, for values the fields above leave open.

| Value | Measured as |
|---|---|
| `x` `y` `w` `h` | Fractions of the parent's inner box (after the parent's `pad`). The root node's parent box is the screen. |
| `size` `pad` `gap` `radius` | Fractions of the screen height. |
| `x` `y` on a child of `row`, `column` or `list` | Ignored: those children flow. |

Missing `w` or `h`:

- Child of `stack`, `pages` or `sequence`: fills the box. A text fills the width and wraps its height.
- Child of `row` or `column`: wraps on the main axis and fills the cross axis.
- A `text` fills the width in a column or stack. Its height always wraps.
- `button`, `icon` and `value` wrap both ways.
- A `round` image with only one size given is a circle of that size.
- A `text` with `scroll` and `maxLines` but no `h` is `maxLines` lines tall. Its full text scrolls inside.

Defaults:

| Node | Default |
|---|---|
| `icon` without `w`/`h` | A square of 1.3 x `size` (default `size` 0.03) of the screen height. |
| `button` padding | 0.02 horizontal and 0.01 vertical (unless `pad` is set). |
| `button` corner radius | 0.01 (unless `radius` is set). |
| `button` background | `#33FFFFFF` (unless `bg` is set). |
| `button` icon | 1.2 x the label text size. |
| `image` placeholder | `#33FFFFFF` (unless `bg` is set) until the image loads. A rejected or failed `src` stays empty. |

Focus is shown as a 2 dp `#4AA3FF` outline over a `#224AA3FF` fill, following the node's shape.

## Nodes

Common fields on every node:

| Field | Meaning |
|---|---|
| `type` | Node type. Unknown types are ignored by the app. |
| `id` | Optional node id. Needed for `value` nodes that are step targets, and reported in events as `elementId`. |
| `x` `y` `w` `h` | Position and size as fractions of the parent box. Omitted = flow inside the container. |
| `pad` | Inner padding. |
| `gap` | Spacing between children. |
| `bg` | Background colour, `#AARRGGBB`. |
| `radius` | Corner radius as a fraction of screen height. |
| `focusable` | Whether the node can take focus. |
| `action` | Action name reported when the node is activated with OK. |
| `showFor` | Id of a focusable element. The node (with its children) is shown only while that element has focus, and hidden otherwise; it takes no space while hidden. Handled locally on each focus move. Focusable nodes inside a `showFor` node never take focus. A passive layout has no focus, so its `showFor` nodes stay hidden. |

Node types:

- `text`: `text`, `size` (fraction of screen height, default `0.03`), `color`, `bold`, `maxLines`, `dir` (`auto` | `ltr` | `rtl`, default `auto`), `scroll` (bool; scrolls with up/down when focused).
- `image`: `src` (absolute URL, or a server-relative path starting with `/`), `fit` (`cover` | `contain`), `round` (bool).
- `icon`: `name` from the bundled set: `play` `pause` `info` `back` `check` `clock` `person` `subtitles` `left` `right` `up` `down` `close`.
- `button`: `label`, `icon`, `action`, `enabled` (default `true`; `false` = greyed and not focusable).
- `row`, `column`, `stack`: `children`.
- `list`: `children`, `orientation` (`horizontal` | `vertical`). Scrolls to keep focus visible.
- `pages`: `children`, `intervalMs` (`0` = only left/right when interactive).
- `sequence`: `children`, each with `durationMs`; `finishedAction` is reported when the last one ends.
- `value`: `value`, `min`, `max`, `scale` (display divisor), `decimals`, `suffix`, `signed` (bool), `changedAction` (debounced 400 ms).

Images: a server-relative `src` is fetched from the Jellyfin server with the user's token. Absolute URLs on other hosts must be `https` and get no token.

## Events

### App to host

`POST /JellyTvOverlay/Event`, JSON body:

```json
{"deviceId":"...","layoutId":"JellyXR/actor","type":"action","action":"openActor","elementId":"p1","value":{"off":250},"reason":null}
```

- `type`: `action` or `closed`.
- `value`: map of `value` node id to number for the layout (current values).
- `reason` (for `closed`): `back`, `ttl`, `finished` or `replaced`.

### Host to client (plugin)

Each is a JSON string passed to the client's handler.

```json
{"type":"trigger","trigger":"pause","deviceId":"d1","sessionId":"s1","itemId":"i1","positionTicks":123456,"userId":"u1"}
{"type":"action","deviceId":"d1","sessionId":"s1","layoutId":"JellyXR/actor","action":"openActor","elementId":"p1","value":{"off":250}}
{"type":"chosen","deviceId":"d1","sessionId":"s1","offerId":"watch","itemId":"i1","positionTicks":123456}
{"type":"closed","deviceId":"d1","layoutId":"JellyXR/actor","reason":"back"}
```

`trigger` is one of `itemStart`, `pause`, `resume`, `doublePause`, `seek`, `stop`. A trigger handler may return `{"offer":{"id":"<offerId>","label":"<text>"}}` or `null`. Other handler results are ignored; clients act through `Show` and `Close`.

## Size

`Show` returns `false` (and logs why) for a layout JSON over 1 MB (UTF-8, preloads included). Poll answers carry non-ASCII text as UTF-8, not `\uXXXX` escapes.

## Stale shows

The host remembers each device's playback state from the latest trigger: paused after `pause` or `doublePause`, playing after `resume`, `itemStart` or `seek`, none after `stop`. `Show` returns `false` (and logs why) for a layout whose `closeOn` contains `resume` while the device is playing, or `stop` while nothing plays: that event already happened, so the layout would never close by itself. Before the first trigger for a device the state is unknown and every layout is allowed.

## HTTP endpoints

All need a Jellyfin token in the `Authorization` header. Poll, Event and Heartbeat return `403` unless the caller has a session on `deviceId` (administrators: any device with a session). Nothing logs headers or tokens.

### `GET /JellyTvOverlay/Poll?deviceId=<id>&since=<seq>`

Long-poll: answers as soon as there are ops after `since`, otherwise after at most 25 s.

```json
{"seq":7,"ops":[
  {"seq":7,"op":"reset","layoutId":""},
  {"seq":7,"op":"show","layoutId":"JellyXR/cast","layout":{"v":1,"id":"JellyXR/cast","owner":"JellyXR","...":"..."}}
]}
```

- `seq`: pass it as `since` in the next poll. With no ops it equals the `since` you sent.
- `op` is `show`, `close` or `reset`. `show` carries the validated `layout` (shown again with the same id = replace); `close` carries only `layoutId`.
- `reset` starts every snapshot: close every window not in this snapshot (the `show` ops that follow, all with the same `seq`). A snapshot is sent for `since=0`, for a `since` above the server's head (server restarted) and for a `since` older than the retained log. On a device that never had a layout, `since=0` waits and then returns `{"seq":0,"ops":[]}`.
- Layouts with `ttlMs` are closed by the host (a `close` op) when they expire.

### `POST /JellyTvOverlay/Event`

Body: an app event (see Events). `204` on success, `400` for malformed JSON or missing `deviceId`/`layoutId`, `403` when the caller may not use the device or `layoutId` is not currently shown there, `413` above 64 KB.

### `POST /JellyTvOverlay/Heartbeat?deviceId=<id>`

Send every 30 s while the app runs. Returns `{"enabled":bool,"protocol":1}`. `enabled` is `false` when the plugin or this device is switched off in the settings; then show nothing. A device counts as having the app (`OverlayHost.HasApp`) for 90 s after its last heartbeat.

### `POST /JellyTvOverlay/Debug/Show?deviceId=<id>` (debug aid)

Administrators only, and `404` unless `DebugEndpoints` is switched on in the plugin settings (default off). Body: a layout JSON; it is shown on the device as client `debug` (ids become `debug/<id>`), through the same validation and rules as `OverlayHost.Show`. Returns `{"shown":bool}`; `400` without `deviceId`, `403` when the device has no session. Events for these layouts are written to the server log.

### `GET /JellyTvOverlay/Clients`, `GET /JellyTvOverlay/Devices`

Administrators only; used by the settings page (registered client plugins; devices with a fresh heartbeat or switched off).

## Example: actor detail ("big card", layout B)

A near full-screen card over the dimmed paused video. Left: portrait, name, birth info, scrollable bio. Right: a "In your library" poster row, and a quick description of the highlighted poster with a disabled Play button. Left/right/up/down are local focus moves; OK on a poster reports the `openTitle` action.

```json
{
  "v": 1,
  "id": "actor-detail",
  "focus": "interactive",
  "dim": 0.7,
  "parent": "cast",
  "closeOn": ["stop", "itemStart", "resume"],
  "suppressTriggers": ["pause", "doublePause"],
  "keys": {
    "back": { "op": "action", "action": "back" }
  },
  "root": {
    "type": "stack",
    "x": 0.05, "y": 0.06, "w": 0.9, "h": 0.88,
    "bg": "#F2121820",
    "radius": 0.02,
    "children": [
      {
        "type": "row", "pad": 0.03, "gap": 0.03,
        "children": [
          {
            "type": "column", "w": 0.34, "gap": 0.015,
            "children": [
              { "type": "image", "id": "portrait", "src": "/Items/p42/Images/Primary", "fit": "cover", "round": true, "h": 0.5 },
              { "type": "text", "text": "Ada Example", "size": 0.05, "bold": true, "maxLines": 2 },
              { "type": "text", "text": "Born 12 March 1975, Haifa", "size": 0.025, "color": "#FFB0B8C4" },
              { "type": "text", "id": "bio", "text": "Long biography text ...", "size": 0.026, "focusable": true, "scroll": true, "maxLines": 8 }
            ]
          },
          {
            "type": "column", "w": 0.63, "gap": 0.02,
            "children": [
              { "type": "text", "text": "In your library", "size": 0.035, "bold": true },
              {
                "type": "list", "orientation": "horizontal", "h": 0.45, "gap": 0.012,
                "children": [
                  { "type": "image", "id": "poster1", "src": "/Items/m1/Images/Primary", "fit": "cover", "radius": 0.008, "focusable": true, "action": "openTitle", "w": 0.2 },
                  { "type": "image", "id": "poster2", "src": "/Items/m2/Images/Primary", "fit": "cover", "radius": 0.008, "focusable": true, "action": "openTitle", "w": 0.2 }
                ]
              },
              {
                "type": "column", "gap": 0.008,
                "children": [
                  { "type": "text", "text": "Example Movie (2019) - 1h 52m", "size": 0.03, "bold": true },
                  { "type": "text", "text": "A short overview in one or two lines.", "size": 0.024, "maxLines": 2 },
                  { "type": "button", "label": "Play", "icon": "play", "enabled": false }
                ]
              }
            ]
          }
        ]
      }
    ]
  },
  "preload": []
}
```

To describe the highlighted poster, give each poster an `id` and put one description per poster in a `stack`, each with `"showFor": "<poster id>"`: only the focused poster's description is shown.

The host rewrites `id` to `JellyXR/actor-detail` and `parent` to `JellyXR/cast` when the owner is `JellyXR`, and sets `owner`.
