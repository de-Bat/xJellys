JellyXR TV: companion app for Jellyfin on Android TV. Pause a movie or episode in Jellyfin and a panel with the cast (portraits, characters, who is on screen) appears over the player; it goes away when you resume.

**New in 1.1.0:** Jellycap recap text cards. With the **Jellycap plugin 1.1.0.0 or newer**, Text recaps pause the episode and show the "Previously on" cards over the player (press play to skip), and Both recaps show each card over its clip.

Needs the **JellyXR plugin 1.4.0.0 or newer** on the Jellyfin server.

**Install**
1. Download the APK below and install it on the TV box (adb, USB stick + file manager, or the Downloader app).
2. Open **JellyXR TV**, press **Find server** (or type the server address) and sign in with **Quick Connect** or user name and password. Use the same user as the Jellyfin app.
3. Press **Allow overlay** and switch JellyXR TV on. If the box has no such setting, from a PC: `adb shell appops set com.xjellys.jellyxr.tv SYSTEM_ALERT_WINDOW allow`
4. Press **Test panel** to check. The app keeps running in the background and starts by itself after a reboot.
