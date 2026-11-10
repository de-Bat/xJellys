JellyTvOverlay: on-screen overlays for Jellyfin on Android TV, driven by the JellyTvOverlay plugin on your server. Replaces JellyXR TV.

**New in 1.0.0:** first release. Cast panel on pause (JellyXR 1.7+) with actor detail (bio and titles in your library), recap text cards (Jellycap 1.3+) and the subtitle sync box (JellySubSync 1.1+). A double pause opens a chooser when both Cast and Sync apply. Test panel and Watch picker included.

Needs the **JellyTvOverlay plugin 1.0.0.0** on the Jellyfin server. **Uninstall JellyXR TV after installing this app** (two panels otherwise); JellyXR TV still works this release.

**Install**
1. Download the APK below and install it on the TV box (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyTvOverlay**, press **Find server** (or type the server address) and sign in with **Quick Connect** or user name and password (https first; plain http only on your local network, shown as "Not secured (local network)"). Use the same user as the Jellyfin app.
3. Press **Allow overlay** and switch JellyTvOverlay on. If the box has no such setting, from a PC: `adb shell appops set com.xjellys.tvoverlay SYSTEM_ALERT_WINDOW allow`
4. Press **Test panel** to check. The app keeps running in the background and starts by itself after a reboot.
