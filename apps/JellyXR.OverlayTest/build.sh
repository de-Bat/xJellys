#!/usr/bin/env bash
# Builds build/jellyxr-overlay-test.apk with the plain Android SDK tools (no Gradle).
# Needs: Android SDK (ANDROID_HOME, or %LOCALAPPDATA%/Android/Sdk), JDK 17+, python3.
set -euo pipefail
cd "$(dirname "$0")"

SDK="${ANDROID_HOME:-${LOCALAPPDATA:-$HOME}/Android/Sdk}"
BT="$SDK/build-tools/$(ls "$SDK/build-tools" | sort -V | tail -1)"
PLATFORM="$SDK/platforms/$(ls "$SDK/platforms" | sort -V | tail -1)"
JAR="$PLATFORM/android.jar"
EXE=""; [ -f "$BT/aapt2.exe" ] && EXE=".exe"
BAT=""; [ -f "$BT/d8.bat" ] && BAT=".bat"
KS="${KEYSTORE:-$HOME/.android/debug.keystore}"

rm -rf build && mkdir -p build/gen build/classes build/dex

"$BT/aapt2$EXE" compile --dir res -o build/res.zip
"$BT/aapt2$EXE" link -I "$JAR" --manifest AndroidManifest.xml -o build/base.apk build/res.zip \
  --java build/gen --min-sdk-version 26 --target-sdk-version 33 --version-code 1 --version-name 0.1

javac -nowarn -Xlint:-options -source 8 -target 8 -bootclasspath "$JAR" -d build/classes \
  $(find src build/gen -name '*.java')

"$BT/d8$BAT" --release --min-api 26 --lib "$JAR" --output build/dex $(find build/classes -name '*.class')

PY="$(command -v python3 || command -v python)"
"$PY" - <<'PY'
import shutil, zipfile
shutil.copy("build/base.apk", "build/unsigned.apk")
with zipfile.ZipFile("build/unsigned.apk", "a") as z:
    z.write("build/dex/classes.dex", "classes.dex")
PY

"$BT/zipalign$EXE" -f -p 4 build/unsigned.apk build/aligned.apk
"$BT/apksigner$BAT" sign --ks "$KS" --ks-pass pass:android --key-pass pass:android \
  --out build/jellyxr-overlay-test.apk build/aligned.apk
"$BT/apksigner$BAT" verify build/jellyxr-overlay-test.apk
ls -l build/jellyxr-overlay-test.apk
