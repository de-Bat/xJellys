# JellyTvOverlay — design

Date: 2026-11-10
Status: draft, awaiting approval

## Intent

Jellyfin for Android TV cannot run plugin UI. Today the repo works around that with one
companion app, **JellyXR TV** (`apps/JellyXR.TV`), that has three hand-coded panels
(`CastPanel`, `RecapCard`, `SyncBox`) and plugin-specific logic (pause and double-pause
detection, polling `JellyXR/Cards`, `Jellycap/Overlay`, `JellySubSync/*`). Every new idea needs
new Java code in the app.

**JellyTvOverlay** replaces that with a generic overlay renderer: plugins describe *what* to show
(layout = text, images, icons, buttons, with position and size) and *when*, the app only draws it
over the Jellyfin player and reports what the user did. Long term it should be a generic Jellyfin
companion app for Android TV overlays that any plugin author can target. Phase 1 serves only this
repo's plugins, but the contract is designed as a public one from day one.

The first new feature built on it is **actor detail**: from the X-Ray cast panel, open an actor
and see their bio and the other titles in *your* library they appear in.

## Goals (phase 1)

1. New Android TV app **JellyTvOverlay**: plugin-agnostic renderer (no plugin names in its code).
2. New Jellyfin plugin **Jellyfin.Plugin.JellyTvOverlay** ("host"): owns the protocol between
   plugins and the app.
3. Versioned **layout format v1**.
4. Move all three existing panels onto it:
   - JellyXR cast panel
   - Jellycap recap text cards
   - JellySubSync subtitle sync box
5. New: **actor detail** (JellyXR) as the first feature built only on the new path.
6. Retire JellyXR TV.

## Non-goals (phase 1)

- Starting playback from actor detail (the button is shown disabled; phase 2).
- Actors / titles not in the user's library; external data (TMDB, etc.).
- Third-party plugin onboarding, docs site, Play Store listing (later; contract must allow it).
- Scripting or logic in the app. The app never decides *what* to show.
- Changing how the Jellyfin client itself looks or behaves (impossible; only Sessions API control).

## Anti-goals (fails even if it "works")

- **Laggy remote or reactions.** Focus moves, scrolling and paging must feel instant.
- **An overlay stealing the remote from the player** when the user didn't step into it.

These two outrank every other design preference.

## Decisions made with the user

- Generic renderer first; actor detail is its first consumer.
- New app (new package id), JellyXR TV retired, not upgraded in place.
- Name: JellyTvOverlay (app and host plugin).
- Existing three panels move to the new renderer in this project.
- Actor detail uses **layout B "big card"**: covers most of the screen over the dimmed paused
  video. Portrait, name, birth info, bio (scrollable), "In your library" poster row; the
  highlighted poster shows a quick description (title, year, runtime, 1–2 lines overview) and a
  disabled Play button. Mockup: `.superpowers/brainstorm/*/content/actor-detail-v2.html`, option B.
- **Plugins choose** when to show something and whether it takes the remote (see Focus).

## Architecture

```
JellyXR / Jellycap / JellySubSync / (future 3rd-party)
        │  show / update / close layouts, subscribe to triggers, receive events
        ▼
Jellyfin.Plugin.JellyTvOverlay (host)
        │  delivers layouts per TV session, routes events to owner plugin,
        │  arbitrates focus, fans out playback triggers
        ▼  (LAN, authenticated as the Jellyfin user)
JellyTvOverlay app (Android TV box) — renders, handles navigation locally, reports actions
```

### App (JellyTvOverlay)

- Background foreground service + boot start, `SYSTEM_ALERT_WINDOW` overlay, same as JellyXR TV
  today. Setup flow kept: LAN discovery / address, Quick Connect or user+password, overlay
  permission (with the `adb shell appops` fallback documented), test panel, "Watch" player picker.
- Follows the Jellyfin session on the same box (existing logic in `WatchService.pick`).
- Renders layout v1 in overlay windows. Holds no plugin knowledge.
- **All navigation is local**: focus movement between focusable nodes, list/row scrolling, text
  scrolling, paging. No server round-trip for any of that.
- Sends **actions** (button activation, item selected with OK, layout dismissed by BACK/timeout)
  to the host.
- Text: `dir: auto` per text node; RTL (Hebrew) must lay out correctly, as today's panels do.
- Images loaded with the app's auth (Jellyfin image URLs work as given), cached, pre-fetched for
  layouts marked as preload.
- Heartbeat to host (replaces the per-plugin `Companion/Heartbeat`) so plugins can tell if a
  device has the app and fall back otherwise.

### Host plugin (Jellyfin.Plugin.JellyTvOverlay)

- The only server API the app talks to.
- Keeps per-session state: which layouts are shown, which one holds the remote.
- **Triggers**: publishes playback events to subscribed plugins: item start, pause, resume,
  double-pause (pause → play → pause within 4 s, as today), seek, stop. (Where detection happens,
  app-side polling vs server session events, is the builder's call; it must meet the latency
  targets. Today double-pause needs app-side fast polling.)
- **Focus arbitration**: only one layout can hold the remote per session. If two plugins request
  the remote for the same trigger, the host shows a small chooser (one button per plugin entry,
  label supplied by each plugin) instead of picking one.
- Routes app actions to the plugin that owns the layout; that plugin answers with
  update/replace/close and/or Jellyfin session commands.
- Plugin settings page: on/off per device or global, list of registered clients.

### Plugin ↔ host contract

- Must work across Jellyfin's per-plugin assembly load contexts (a shared contract DLL referenced
  by both sides can load twice and fail type identity). Use only BCL types at the boundary
  (e.g. JSON strings + `Func`/`Action` delegates found by reflection) or a loopback HTTP API. Builder
  picks; it must be usable by a third-party plugin without referencing this repo's code.
- Operations: register client (id, display name), subscribe to triggers, show(session, layout),
  update(layoutId, patch or full), close(layoutId), receive actions, query "does this session
  have the app".
- If the host plugin is missing or the device has no app: each client plugin keeps today's
  fallback (JellyXR → `DisplayMessage` toasts; Jellycap → ffmpeg-rendered recap video; SubSync →
  no box). *(my call)*

## Layout format v1

JSON, carries `"v": 1`. Unknown node types / fields are ignored by the app (forward compatible).

Layout fields: `id`, `owner`, `focus` (`passive` | `interactive` | `interactive+passthrough`),
`ttlMs` (0 = until closed), `dim` (dim video behind, 0–1), `preload` (layouts/images to fetch
ahead), `root` node.

Node types: `text`, `image`, `icon`, `button`, `row`, `column`, `stack`, `list` (scrollable, with
focusable items). Geometry in screen fractions (`x, y, w, h`) or flow inside containers; anchors;
padding; background colour; corner radius; text size (relative to screen height); `maxLines`;
`dir`. Interactive nodes carry `id` and `action`; `enabled: false` shows them greyed (used for
phase-1 Play).

A small fixed icon set (play, info, back, check, clock, person, subtitles, …) is bundled; no
remote icon fonts.

Local behaviour the app runs without the server (needed so keys never wait on the network)
*(my calls, from the builder check)*:

- **Key bindings**: a layout may bind keys (◀▶▲▼, OK, PLAY, BACK) to either local operations or
  actions sent to the owner. Unbound arrows move focus as usual.
- **Value node** (`value` / stepper): local numeric state with min/max, step per bound key,
  display format (e.g. `+1.25 s`). Each change re-renders instantly; the current value is sent
  with the next action (e.g. "apply") and optionally debounced as a `changed` action. This is
  what the SubSync box uses.
- **Pages container**: children shown one at a time, auto-advance interval (cast panel: 8 s,
  today's `PAGE_MS`) or ◀▶ when interactive.
- **Sequence container**: children shown in order with a per-child duration (Jellycap cards'
  `At`/`Seconds`), then a `finished` action to the owner.
- **Close-to-parent**: a layout can name a parent layout id; BACK closes it and returns focus to
  the parent (actor detail → cast panel).

Exact field names and schema are the builder's call, but the format is published (a doc file in
the repo) as the contract third-party plugins will target.

## Focus and the remote

- `passive`: never takes the remote. Default.
- `interactive`: takes the remote; BACK closes (or goes back one level if the owner set a parent).
- `interactive+passthrough`: takes the remote, but PLAY/PAUSE (and seek keys if the layout doesn't
  use ◀▶) are forwarded to the player through the Sessions API. Acceptable added delay on resume
  ≈ 0.3 s.
- Each plugin decides per layout and per trigger.
- Defaults in this project *(my calls)*:
  - JellyXR cast panel on pause: `passive`, a JellyXR setting makes it `interactive+passthrough`.
  - **Stepping in** (the way into actor detail by default): double-pause → chooser → "Cast"
    opens the cast panel as `interactive`. With the JellyXR setting on, the pause panel is already
    interactive and OK on an actor works directly.
  - Double-pause: JellyXR ("Cast") and SubSync ("Sync subtitles") both request the remote → host
    chooser. If only one applies (e.g. no subtitle track / sync not enabled for the title) it
    opens directly.
  - SubSync box: `interactive`, same keys as today (◀▶ 0.25 s, ▲▼ 1 s, OK/play apply, BACK close,
    15 s idle close).
  - Jellycap text cards: `passive`, same behaviour as today (episode paused while cards show, play
    skips; Jellycap handles that via its own trigger handling).

## Latency targets

- Key press → visible focus/scroll change: one frame, local, never network-bound.
- OK on an actor → actor detail visible: ≤ 150 ms (detail data pre-loaded when the cast panel is
  shown).
- Pause → passive cast panel visible: no worse than today (~1 s).
- Double-pause → chooser / box: no worse than today.
- Action → server response on LAN: ≤ 300 ms typical.

## Actor detail (JellyXR, first consumer)

- Entry: from the interactive cast panel (▲▼ choose actor, OK).
- Content, from the user's Jellyfin server only:
  - Person image, name, birth date + place if known (Jellyfin person `PremiereDate`,
    `ProductionLocations`), bio (`Overview`), scrollable with ▲▼ when focused.
  - "In your library": movies and series the person appears in, visible to the user. Episodes
    roll up to their series (one poster per series). Exclude the title currently playing
    *(my call)*. Order: newest first *(my call)*.
  - Highlighted poster → quick description: title, year, runtime (movies) / seasons (series),
    first 1–2 lines of overview, disabled "Play".
- Navigation: ▲▼ between bio and poster row, ◀▶ along the row, BACK → cast panel → close.
  Playback stays paused throughout.
- JellyXR builds the full detail layouts for all actors in the panel when the panel is shown
  **interactive** (not on every passive pause, to avoid server and image load) and marks them
  `preload`, so opening one is instant. *(my call)*

## SubSync "apply" sequence

Today the app does stop → wait → restart → switch to the synced track with retries (and avoids a
remote PlayNow because it crashes the Jellyfin client, see 1.2.2). This moves into
JellySubSync on the server, driven by session events and Sessions API commands *(my call)*.
The app keeps no SubSync logic. If the builder finds server-side track switching unreliable,
the fallback is a **generic** app capability (e.g. "after the session restarts, select subtitle
stream N on it"), added to the contract, not SubSync-specific code. Behaviour as seen by the
user must stay as in JellyXR TV 1.2.2.

## Migration / retirement

- JellyXR, Jellycap, JellySubSync each switch to host clients; existing user-visible behaviour
  stays the same unless stated above.
- Old JellyXR TV endpoints (`JellyXR/Cards`, `Jellycap/Overlay*`, `Jellycap/Companion/Heartbeat`,
  `JellySubSync/*` used by the app) stay working for one release so boxes still on JellyXR TV
  don't break, then are removed *(my call)*.
- README: JellyXR TV section replaced by JellyTvOverlay; note to uninstall JellyXR TV.
- New release tags/workflow for the app following the existing `app-…` scheme; plugin added to
  `manifest.json` like the others.

## Constraints

- Jellyfin server: same target as existing plugins (net8.0, `Jellyfin.Controller` 10.9.x,
  compatible with 10.11 — see `ServerCompat`/`LibraryCompat` patterns).
- App build: existing no-Gradle `build.sh` (Android SDK + JDK 17, Java source 8, no lambdas). Keep
  that approach.
- Android TV boxes with weak GPUs: no blur/heavy effects; "dim" is a flat translucent layer.
- Some boxes lack the overlay permission UI (adb fallback).

## Left to the builder

- Transport between app and host (SSE / long-poll / WebSocket / fast poll), as long as the latency
  targets hold.
- Where trigger detection runs (app vs server events).
- Exact JSON schema names, plugin↔host interop mechanism (within the constraint above).
- Internal app architecture, caching, image loading.
- Test strategy.

## Phase 2+ (not now)

- Play from actor detail (and generally: layout actions that start playback on the session).
- Third-party plugin docs, sample plugin, discoverability.
- Possibly external person data.
