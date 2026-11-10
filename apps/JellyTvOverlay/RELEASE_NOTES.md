JellyTvOverlay: on-screen overlays for Jellyfin on Android TV, driven by the JellyTvOverlay plugin on your server.

**New in 0.1.0:** first scaffold. Sign in with Quick Connect or user name and password (https first; plain http only on your local network and shown as "Not secured (local network)"), allow the overlay permission and pick which Jellyfin player to follow. The overlay renderer arrives in a later version.

**Install**
1. Download the APK below and install it on the TV box (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyTvOverlay**, press **Find server** (or type the server address) and sign in. Use the same user as the Jellyfin app.
3. Press **Allow overlay** and switch JellyTvOverlay on. If the box has no such setting, from a PC: `adb shell appops set com.xjellys.tvoverlay SYSTEM_ALERT_WINDOW allow`
