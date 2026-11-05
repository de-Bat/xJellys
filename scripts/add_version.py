#!/usr/bin/env python3
"""Add a plugin release to manifest.json.

Usage:
  add_version.py --zip Jellyfin.Plugin.Foo_1.0.0.0.zip --url https://.../Foo_1.0.0.0.zip \
      --guid <plugin-guid> --version 1.0.0.0 [--abi 10.9.0.0] [--changelog "text"] \
      [--name "Foo" --description "..." --overview "..." --owner "me" --category General]

New plugin (guid not in manifest) requires --name. Existing plugin: version is prepended.
"""
import argparse, hashlib, json, sys
from datetime import datetime, timezone
from pathlib import Path

MANIFEST = Path(__file__).resolve().parent.parent / "manifest.json"

p = argparse.ArgumentParser()
p.add_argument("--zip", required=True)
p.add_argument("--url", required=True)
p.add_argument("--guid", required=True)
p.add_argument("--version", required=True)
p.add_argument("--abi", default="10.9.0.0")
p.add_argument("--changelog", default="")
p.add_argument("--name")
p.add_argument("--description", default="")
p.add_argument("--overview", default="")
p.add_argument("--owner", default="")
p.add_argument("--category", default="General")
p.add_argument("--image-url", default="")
a = p.parse_args()

md5 = hashlib.md5(Path(a.zip).read_bytes()).hexdigest().upper()
ver = {
    "version": a.version,
    "changelog": a.changelog,
    "targetAbi": a.abi,
    "sourceUrl": a.url,
    "checksum": md5,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

data = json.loads(MANIFEST.read_text(encoding="utf-8"))
plugin = next((x for x in data if x["guid"].lower() == a.guid.lower()), None)
if plugin is None:
    if not a.name:
        sys.exit("new plugin: --name required")
    plugin = {
        "guid": a.guid, "name": a.name, "description": a.description,
        "overview": a.overview or a.description, "owner": a.owner,
        "category": a.category, "imageUrl": a.image_url, "versions": [],
    }
    data.append(plugin)

plugin["versions"] = [v for v in plugin["versions"] if v["version"] != a.version]
plugin["versions"].insert(0, ver)
MANIFEST.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
print(f"{plugin['name']} {a.version} md5={md5}")
