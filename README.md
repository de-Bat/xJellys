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
