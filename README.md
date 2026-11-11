# xJellys – Jellyfin Plugin Repository

## Install
Jellyfin Dashboard → Plugins → Repositories → **+** and add:

```
https://raw.githubusercontent.com/de-Bat/xJellys/main/manifest.json
```

Then Catalog → install plugin → restart Jellyfin.

## Publish a plugin release
1. Build plugin, zip DLL(s) (+ `meta.json`): `Jellyfin.Plugin.Foo_1.0.0.0.zip`.
2. Upload zip as GitHub Release asset.
3. Register it:
   ```
   python scripts/add_version.py --zip Foo_1.0.0.0.zip \
     --url https://github.com/de-Bat/xJellys/releases/download/v1.0.0.0/Foo_1.0.0.0.zip \
     --guid <PLUGIN-GUID> --version 1.0.0.0 --abi 10.9.0.0 \
     --name "Foo" --description "..." --owner "de-Bat"
   ```
   (`--name` etc. only needed first time.)
4. Commit + push `manifest.json`.

Checksum = MD5 of zip (uppercase); script computes it.

## Automated release (GitHub Action)
Plugins live in `plugins/<Folder>/` (copy `plugins/Jellyfin.Plugin.Template`; change GUID in `Plugin.cs`,
`configPage.html`, `meta.json`). Metadata for manifest comes from `meta.json`.

```
git tag Jellyfin.Plugin.Template-v1.0.0.0
git push --tags
```
Tag format `<Folder>-v<version>`. Action builds, zips, creates Release, commits `manifest.json` to `main`.

## Logs
Each plugin also writes its own log file in the Jellyfin log folder (Dashboard → Logs): `jellyxr.log`, `jellycap.log`, `jellysubsync.log`. Contains Debug and up (the server log only shows what its level allows). Rolls to `.log.1` at 10 MB.

## Plugins

### Jellycap (`plugins/Jellyfin.Plugin.Jellycap`)
Plays a recap before an episode on **Android TV** (any remote-controllable client; filter in plugin settings).

- **New episode** → recap = tail of the previous N episodes (credits skipped).
- **Resumed episode** → previous-episode recap + clips spread over the part of this episode already watched, ending at the resume point.
- *Recap when resuming an episode* (global, overridable per show as *When resuming*): `Previous episodes + this episode so far` (default) | `Previous episodes only` (same recap as a fresh start, then the episode resumes at its position) | `This episode so far only`. In **Text** recaps the this-episode part is dialogue cards (see below).

Android TV cannot show plugin UI, so Jellycap builds a real video with ffmpeg and tells the TV session to play it, then returns to the episode (at the resume position).

Setup:
1. Install from the repo catalog, restart Jellyfin.
2. Add the recap folder (default `<jellyfin data>/jellycap`, change in plugin settings) as a library (Movies or Home Videos, disable metadata downloaders).
3. Play an episode on Android TV. The recap for the *next* episode is pre-built, so it is instant when binging; the first episode waits up to `MaxWaitSeconds` for a build, else it just plays normally.

Text recap (`Recap format` setting: `Video` | `Text` | `Both`):
- **Text** = title cards ("Previously on …", then `S01E02 · Name` + episode summary from Jellyfin metadata) rendered by ffmpeg/libass, so right-to-left scripts (Hebrew, Arabic) shape correctly. Reading time scales with text length.
- **Watched part of a resumed episode as text**: the summary would spoil the rest, so the cards quote the episode's **subtitles** instead: "Earlier in this episode", then one card per moment (`S01E03 · 12:34` + the last lines spoken there, up to 4), spread over the watched part like the clips (`ThisEpisodeClips` moments, 20 s of dialogue each, the last ending at the resume point). Uses a text subtitle track: full (not forced) tracks first, then one in the card language, then the default track. External `srt/vtt/ass/ssa` are read directly; embedded text tracks are extracted once with ffmpeg and cached (`<jellyfin cache>/jellycap-subtitles`). A track in another language is machine-translated when a translation server is set. No text subtitle track = no dialogue cards (a *this episode only* text recap is then skipped).
- **Both** = a card before each video clip (for the watched part: the dialogue of that clip).
- **Translation**: card labels are localized (`en he es fr de it pt ru ar`). Summaries are shown in the language stored in Jellyfin; set a *Translation server URL* (LibreTranslate compatible `POST /translate`) to machine-translate them to the chosen language. Translations are cached on disk; if the server is down the original text is used.
- **TV app** (setting *Show text cards with the JellyXR TV app*, default on). Jellycap 1.3+ shows the cards through **JellyTvOverlay** (see below); the older JellyXR TV app (1.1.0+) still works this release. The app draws the cards over the player instead of ffmpeg burning them into a video. **Text** = no recap video: the episode pauses, the cards show one after another, then it goes back to its start position and plays (press play to skip). **Both** = the clips-only recap video plays with each card over its clip. Without the app (or with JellyXR TV not running) recaps are videos as before.
- **Fonts**: libass uses the server's fonts. Set *Text card font* to one that covers your language (e.g. `DejaVu Sans` on Linux for Hebrew; Windows `Arial` works).

Choosing what gets a recap (plugin settings → **TV shows**):
- Pick a show from the dropdown and add it as a rule. Per rule: switch the show off, override the format (Video/Text/Both), limit **Seasons** (`1,3-5`, `2-`, `0` = specials) and **Episodes** (numbers within the allowed seasons, e.g. `2-` = skip each season opener).
- *Only recap the shows listed below* turns the list into a whitelist. Otherwise every show gets recaps and the list only holds exceptions.
- Shows without a rule use the global *Default recap format*.

Automatic pre-building: when an episode **stops** (setting *Build recaps when playback stops*), mid-way past `Resume min seconds` → its resume recap is built for that exact position; finished (or ≥ 90%) → the **next** episode's recap is built. Starting an episode also pre-builds the next one's recap. Next time you press play the recap is already cached.

Limits: the client starts the episode before the server can intercept, so the recap appears a moment after playback starts. Backing out of the recap (stop) does not auto-start the episode. Needs Jellyfin 10.9+ and episodes with a local file.

Release: `git tag Jellyfin.Plugin.Jellycap-v1.3.0.0 && git push --tags`.

### JellyXR (`plugins/Jellyfin.Plugin.JellyXR`)
Amazon X-Ray style cast cards for **Android TV** (any remote-controllable client; filter in plugin settings). Pause playback and the actors appear as a message on the TV.

- **How "on screen" works**: a background task samples each episode/movie (one frame per `ScanIntervalSeconds`), detects faces (SCRFD) and matches them against the cast's Jellyfin portraits (ArcFace) using ONNX Runtime on CPU. The result is a per-item timeline stored in `<jellyfin data>/jellyxr/timelines`. Models (insightface `buffalo_s`, URL configurable, check its license) are downloaded on first use.
- **Scope** (global default, per-show override): `Scene` = people seen around the pause position, `Episode` = whole cast, `Current` = scene, or whole cast if no scene data, `Off`.
- **Cards**: `Combined` (one message) or `Separate` (one message per actor), "Actor as Character".
- Scan runs from Dashboard → Scheduled Tasks → *Scan videos for cast faces* (daily 03:00), and in the background when an unscanned item starts playing. Needs cast portraits in Jellyfin; actors without a portrait face can't be matched.
- Cards are sent as the Jellyfin `DisplayMessage` command; how the Android TV client renders it (toast/dialog, timeout) depends on the client version.
- **Xrayarr** (`Data source` setting): scene data can come from an [Xrayarr](https://github.com/de-Bat/xrayarr) server instead of the local scan. `Local` (default) = local face scan only; `Xrayarr` = Xrayarr only (no local scan, no model download); `XrayarrThenLocal` = Xrayarr, local scan for items Xrayarr has no ready timeline for; `LocalThenXrayarr` = local timeline, Xrayarr for items without one. Set *Xrayarr URL* and *API key* (Xrayarr Settings → General). Lookup: movies by TMDB id, episodes by series TVDB id + season/episode, then by file path (optional *path rewrite* `jellyfinPrefix=xrayarrPrefix` if Jellyfin and Xrayarr mount media differently). The timeline is fetched once when playback starts and cached (6 h; 2 min for misses), so pausing needs no extra request. Xrayarr people not in Jellyfin's cast are still shown with Xrayarr's character name.
- **X-Ray marker** (setting *Mark movies and episodes that have X-Ray data*, on by default): items with scene data (local timeline or ready Xrayarr timeline) get a tag and join a collection, both named *Marker name* (default `X-Ray`). The Android TV app can't run plugin UI but shows these: open **Collections → X-Ray** to see everything with X-Ray, or filter a library by the tag. Opt-in *Also add it as a genre* puts `X-Ray` in the genre line of the movie/episode page (the only spot the TV app shows it there; it mixes into real genres and a "replace all metadata" refresh drops it until the next run). Updated after each scan, when an Xrayarr item starts playing, and by Scheduled Tasks → *Mark items with X-Ray data* (daily 05:00; run it by hand after changing these settings or turning marking off, which removes the markers).
- Native ONNX Runtime ships for `win-x64` and `linux-x64` only. Release tag: `Jellyfin.Plugin.JellyXR-v1.7.0.0`.
- **TV app** (optional): Android TV toasts are 2 lines for 3.5 s. Install the **JellyTvOverlay** app (JellyXR 1.7+ and the JellyTvOverlay plugin) and pausing shows a panel with portraits over the Jellyfin player instead (the plugin then stops sending toasts to that device). Select an actor for a detail view: bio and titles in your library (Play comes later). With the setting *interactive on pause* the panel opens on pause with ▲ ▼ / OK on the cast rows, while Play/Pause and ◀ ▶ (seek) keep going to the player. A double pause opens it fully interactive (it holds the remote; ◀ ▶ flip pages). BACK always closes the panel and gives the remote back. The older **JellyXR TV** app (`apps/JellyXR.TV`) is deprecated but still works this release.

### JellySubSync (`plugins/Jellyfin.Plugin.JellySubSync`)
Shift subtitles earlier or later from the TV remote, through the **JellyTvOverlay** app (JellySubSync 1.1+; the older JellyXR TV app 1.2.0+ still works this release).

- **Opt-in** (setting *Subtitle sync for*): `Off` (default, nothing changes), `All` movies and episodes, or `Selected` = only checked **libraries** plus picked **shows** (all episodes) and **movies** (search box in the settings). For titles without sync a double pause is just a pause: no box, the cast panel shows as usual.
- **On the TV**: in Jellyfin for Android TV, with a subtitle track on, **pause, play and pause again within 4 s**. When both Cast (JellyXR) and Sync apply, the double pause opens a small chooser first. A sync box opens over the player and takes the remote: ◀ / ▶ = 0.25 s, ▲ / ▼ = 1 s (+ = subtitles later), **OK** or play = apply, **BACK** = close (closes by itself after 15 s idle). Applying restarts playback at the paused spot on the synced track (a 1–3 s reload).
- **What it writes**: a shifted copy of the track, `<video name>.<lang>.Synced.<ext>`, which Jellyfin lists as an extra track titled *Synced*. The original track is never changed. Every shift is made from the original (no rounding drift). Offset 0 = back to the original and the copy is deleted. The offset is saved per item and track (`<jellyfin data>/jellysubsync`), and the box opens at the current value.
- **Where**: by default in the item's Jellyfin metadata folder, so the media folders can stay read-only. Setting *Save next to the video* writes it beside the video file instead (needs write access for the Jellyfin user: Docker = media volume without `:ro`; Linux = `setfacl -R -m u:jellyfin:rwX <media>`; Windows = Modify rights for the service account).
- **Tracks**: text subtitles only: external SRT, ASS/SSA, WebVTT, and embedded text tracks (extracted once with Jellyfin's ffmpeg and cached; the first sync of an embedded track can take a while on big files). Picture subtitles (PGS, VobSub) can't be shifted. File encodings are kept as is (only timestamps change).
- The synced track is shared by all users; *Administrators only* restricts who may shift.

Release: `git tag Jellyfin.Plugin.JellySubSync-v1.1.0.0 && git push --tags`.

### JellyTvOverlay (`plugins/Jellyfin.Plugin.JellyTvOverlay`)
Host plugin for the JellyTvOverlay app. Other plugins (JellyXR, Jellycap, JellySubSync) send it overlay layouts; the host delivers them to the TV box and routes button presses back. Install it from the catalog together with the plugins you want on the TV, then restart Jellyfin.

Settings (Dashboard → Plugins → JellyTvOverlay): *Enabled* (server-wide switch), per-device on/off, *Debug endpoints* (admin-only test endpoint, off by default), and the list of registered clients (boxes running the app). Release: `git tag Jellyfin.Plugin.JellyTvOverlay-v1.0.0.0 && git push --tags`.

**For plugin authors:** the JSON format is in [`docs/jellytvoverlay/layout-v1.md`](docs/jellytvoverlay/layout-v1.md); [`plugins/shared/OverlayClient.cs`](plugins/shared/OverlayClient.cs) is the shared client to link into a plugin so it can send overlays to the host.

Security:
- The access token is never put in URLs or logs.
- App data is excluded from Android backups.
- A box can only reach overlays for devices that have a session of the signed-in user. A device id is trusted as "has a session claiming this id" (Jellyfin's own model).

### JellyTvOverlay app (`apps/JellyTvOverlay`)
Android TV companion app. It runs in the background, follows the Jellyfin for Android TV session on the same box (same device name; otherwise pick the player once with Watch, it never follows another box), and draws the overlays the server plugins send (cast panel, recap cards, subtitle sync box) over the player. The remote keeps controlling Jellyfin unless an overlay takes it. Needs the JellyTvOverlay plugin; JellyXR 1.7+, Jellycap 1.3+ and JellySubSync 1.1+ add the features. A double pause opens a chooser when both Cast and Sync apply.

1. Download `JellyTvOverlay-<version>.apk` from the [releases](https://github.com/de-Bat/xJellys/releases) and install it (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyTvOverlay**, find the server (LAN discovery) or type its address. https is preferred; plain http is accepted only on the local network and shown as "Not secured (local network)". Sign in with **Quick Connect** (or user name and password; use the same user as the Jellyfin app).
3. **Allow overlay**. If the box has no such setting: `adb shell appops set com.xjellys.tvoverlay SYSTEM_ALERT_WINDOW allow`.
4. Press **Test panel** to see it. It starts by itself after a reboot. **Watch** picks another player if automatic picks the wrong one.

**Uninstall JellyXR TV after installing JellyTvOverlay** (with both installed you get two panels). The old JellyXR TV app (`apps/JellyXR.TV`) is deprecated but keeps working this release.

Build locally with `apps/JellyTvOverlay/build.sh` (Android SDK + JDK 17, no Gradle); tests: `apps/JellyTvOverlay/test.sh`. Release: `git tag app-JellyTvOverlay-v1.0.0 && git push origin app-JellyTvOverlay-v1.0.0` (needs repo secrets `ANDROID_KEYSTORE_B64` and `ANDROID_KEYSTORE_PASS`).

### JellyXR TV (`apps/JellyXR.TV`) - deprecated
Older companion app, replaced by JellyTvOverlay. Still works this release with JellyXR 1.4+, Jellycap 1.1+ and JellySubSync 1.0+; the plugins send to whichever app is installed. Install JellyTvOverlay and uninstall this one. Build: `apps/JellyXR.TV/build.sh`. Tag: `app-JellyXR.TV-v1.0.0`.
