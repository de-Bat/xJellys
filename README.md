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

Limits: the client starts the episode before the server can intercept, so the recap appears a moment after playback starts. Backing out of the recap (stop) does not auto-start the episode. Needs Jellyfin 10.9+ and episodes with a local file.

Release: `git tag Jellyfin.Plugin.Jellycap-v1.0.0.0 && git push --tags`.
