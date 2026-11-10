JellyXR TV: companion app for Jellyfin on Android TV. Pause a movie or episode in Jellyfin and a panel with the cast (portraits, characters, who is on screen) appears over the player; it goes away when you resume.

**New in 1.2.1:** subtitle sync restart fixed. After OK the app now stops the player, starts the title fresh at the same spot and then switches it to the "Synced" track once it plays (retried a few times). Before, the Jellyfin app restarted on the original track and its subtitle menu could miss the tracks.

**New in 1.2.0:** subtitle sync. With the **JellySubSync plugin 1.0.0.0** on the server, pause, play and pause again within 4 seconds: a sync box opens over the player. Left / right moves the subtitles 0.25 s, up / down 1 s, OK applies and resumes on the synced track, BACK closes. While the box is up the remote drives the box, not the player.

**New in 1.1.0:** Jellycap recap text cards. With the **Jellycap plugin 1.1.0.0 or newer**, Text recaps pause the episode and show the "Previously on" cards over the player (press play to skip), and Both recaps show each card over its clip.

Needs the **JellyXR plugin 1.4.0.0 or newer** on the Jellyfin server.

**Install**
1. Download the APK below and install it on the TV box (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyXR TV**, press **Find server** (or type the server address) and sign in with **Quick Connect** or user name and password. Use the same user as the Jellyfin app.
3. Press **Allow overlay** and switch JellyXR TV on. If the box has no such setting, from a PC: `adb shell appops set com.xjellys.jellyxr.tv SYSTEM_ALERT_WINDOW allow`
4. Press **Test panel** to check. The app keeps running in the background and starts by itself after a reboot.
