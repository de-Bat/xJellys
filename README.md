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
Each plugin also writes its own log file in the Jellyfin log folder (Dashboard → Logs): `jellyxr.log`, `jellycap.log`. Contains Debug and up (the server log only shows what its level allows). Rolls to `.log.1` at 10 MB.

## Plugins

### Jellycap (`plugins/Jellyfin.Plugin.Jellycap`)
Plays a recap before an episode on **Android TV** (any remote-controllable client; filter in plugin settings).

- **New episode** → recap = tail of the previous N episodes (credits skipped).
- **Resumed episode** → previous-episode recap + clips spread over the part of this episode already watched, ending at the resume point.

Android TV cannot show plugin UI, so Jellycap builds a real video with ffmpeg and tells the TV session to play it, then returns to the episode (at the resume position).

Setup:
1. Install from the repo catalog, restart Jellyfin.
2. Add the recap folder (default `<jellyfin data>/jellycap`, change in plugin settings) as a library (Movies or Home Videos, disable metadata downloaders).
3. Play an episode on Android TV. The recap for the *next* episode is pre-built, so it is instant when binging; the first episode waits up to `MaxWaitSeconds` for a build, else it just plays normally.

Text recap (`Recap format` setting: `Video` | `Text` | `Both`):
- **Text** = title cards ("Previously on …", then `S01E02 · Name` + episode summary from Jellyfin metadata) rendered by ffmpeg/libass, so right-to-left scripts (Hebrew, Arabic) shape correctly. Reading time scales with text length. A resumed episode gets the same cards as a new one (a spoiler-free recap of the watched part needs video).
- **Both** = a card before each video clip.
- **Translation**: card labels are localized (`en he es fr de it pt ru ar`). Summaries are shown in the language stored in Jellyfin; set a *Translation server URL* (LibreTranslate compatible `POST /translate`) to machine-translate them to the chosen language. Translations are cached on disk; if the server is down the original text is used.
- **Fonts**: libass uses the server's fonts. Set *Text card font* to one that covers your language (e.g. `DejaVu Sans` on Linux for Hebrew; Windows `Arial` works).

Choosing what gets a recap (plugin settings → **TV shows**):
- Pick a show from the dropdown and add it as a rule. Per rule: switch the show off, override the format (Video/Text/Both), limit **Seasons** (`1,3-5`, `2-`, `0` = specials) and **Episodes** (numbers within the allowed seasons, e.g. `2-` = skip each season opener).
- *Only recap the shows listed below* turns the list into a whitelist. Otherwise every show gets recaps and the list only holds exceptions.
- Shows without a rule use the global *Default recap format*.

Automatic pre-building: when an episode **stops** (setting *Build recaps when playback stops*), mid-way past `Resume min seconds` → its resume recap is built for that exact position; finished (or ≥ 90%) → the **next** episode's recap is built. Starting an episode also pre-builds the next one's recap. Next time you press play the recap is already cached.

Limits: the client starts the episode before the server can intercept, so the recap appears a moment after playback starts. Backing out of the recap (stop) does not auto-start the episode. Needs Jellyfin 10.9+ and episodes with a local file.

Release: `git tag Jellyfin.Plugin.Jellycap-v1.0.0.0 && git push --tags`.

### JellyXR (`plugins/Jellyfin.Plugin.JellyXR`)
Amazon X-Ray style cast cards for **Android TV** (any remote-controllable client; filter in plugin settings). Pause playback and the actors appear as a message on the TV.

- **How "on screen" works**: a background task samples each episode/movie (one frame per `ScanIntervalSeconds`), detects faces (SCRFD) and matches them against the cast's Jellyfin portraits (ArcFace) using ONNX Runtime on CPU. The result is a per-item timeline stored in `<jellyfin data>/jellyxr/timelines`. Models (insightface `buffalo_s`, URL configurable, check its license) are downloaded on first use.
- **Scope** (global default, per-show override): `Scene` = people seen around the pause position, `Episode` = whole cast, `Current` = scene, or whole cast if no scene data, `Off`.
- **Cards**: `Combined` (one message) or `Separate` (one message per actor), "Actor as Character".
- Scan runs from Dashboard → Scheduled Tasks → *Scan videos for cast faces* (daily 03:00), and in the background when an unscanned item starts playing. Needs cast portraits in Jellyfin; actors without a portrait face can't be matched.
- Cards are sent as the Jellyfin `DisplayMessage` command; how the Android TV client renders it (toast/dialog, timeout) depends on the client version.
- **Xrayarr** (`Data source` setting): scene data can come from an [Xrayarr](https://github.com/de-Bat/xrayarr) server instead of the local scan. `Local` (default) = local face scan only; `Xrayarr` = Xrayarr only (no local scan, no model download); `XrayarrThenLocal` = Xrayarr, local scan for items Xrayarr has no ready timeline for; `LocalThenXrayarr` = local timeline, Xrayarr for items without one. Set *Xrayarr URL* and *API key* (Xrayarr Settings → General). Lookup: movies by TMDB id, episodes by series TVDB id + season/episode, then by file path (optional *path rewrite* `jellyfinPrefix=xrayarrPrefix` if Jellyfin and Xrayarr mount media differently). The timeline is fetched once when playback starts and cached (6 h; 2 min for misses), so pausing needs no extra request. Xrayarr people not in Jellyfin's cast are still shown with Xrayarr's character name.
- Native ONNX Runtime ships for `win-x64` and `linux-x64` only. Release tag: `Jellyfin.Plugin.JellyXR-v1.0.0.0`.
- **JellyXR TV app** (`apps/JellyXR.TV`, optional): Android TV toasts are 2 lines for 3.5 s. Install the companion APK on the box and pausing shows a panel with portraits over the Jellyfin player instead (the plugin then stops sending toasts to that device). See below.

### JellyXR TV (`apps/JellyXR.TV`)
Companion app for Android TV boxes. It runs in the background, follows the Jellyfin for Android TV session on the same box (polls `/Sessions` every second), and when playback pauses it draws the cast panel from `JellyXR/Cards` over the player. The remote keeps controlling Jellyfin. Needs JellyXR 1.4+.

1. Download `JellyXR.TV-<version>.apk` from the [releases](https://github.com/de-Bat/xJellys/releases) and install it (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyXR TV**, find the server (LAN discovery) or type its address, sign in with **Quick Connect** (or user name and password; use the same user as the Jellyfin app).
3. **Allow overlay**. If the box has no such setting: `adb shell appops set com.xjellys.jellyxr.tv SYSTEM_ALERT_WINDOW allow`.
4. Press **Test panel** to see it. It starts by itself after a reboot. **Watch** picks another player if automatic picks the wrong one.

Plugin setting *JellyXR TV app: on pause show the portraits panel* turns it off server-wide. Build locally with `apps/JellyXR.TV/build.sh` (Android SDK + JDK 17, no Gradle). Release: `git tag app-JellyXR.TV-v1.0.0 && git push origin app-JellyXR.TV-v1.0.0` (needs repo secrets `ANDROID_KEYSTORE_B64` and `ANDROID_KEYSTORE_PASS`).
