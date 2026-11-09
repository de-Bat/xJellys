#!/usr/bin/env bash
# Builds build/jellyxr-tv.apk with the plain Android SDK tools (no Gradle).
# Needs: Android SDK (ANDROID_HOME, or %LOCALAPPDATA%/Android/Sdk), JDK 17+, python3.
# Env: VERSION (e.g. 1.0.0, default 0.0.0-dev), KEYSTORE / KEYSTORE_PASS / KEY_ALIAS (default: debug keystore).
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${VERSION:-0.0.0-dev}"
# 1.2.3 -> 10203; dev builds -> 1
CODE="$(echo "$VERSION" | awk -F'[.-]' '/^[0-9]+\.[0-9]+\.[0-9]+/ { print $1*10000 + $2*100 + $3; next } { print 1 }')"

SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-${LOCALAPPDATA:-$HOME}/Android/Sdk}}"
BT="$SDK/build-tools/$(ls "$SDK/build-tools" | sort -V | tail -1)"
PLATFORM="$SDK/platforms/$(ls "$SDK/platforms" | grep -E '^android-[0-9]+' | sort -V | tail -1)"
JAR="$PLATFORM/android.jar"
EXE=""; [ -f "$BT/aapt2.exe" ] && EXE=".exe"
BAT=""; [ -f "$BT/d8.bat" ] && BAT=".bat"
KS="${KEYSTORE:-$HOME/.android/debug.keystore}"
KS_PASS="${KEYSTORE_PASS:-android}"
ALIAS="${KEY_ALIAS:-}"

rm -rf build && mkdir -p build/gen/com/xjellys/jellyxr/tv build/classes build/dex
cat > build/gen/com/xjellys/jellyxr/tv/BuildInfo.java <<JAVA
package com.xjellys.jellyxr.tv;
final class BuildInfo { static final String VERSION = "$VERSION"; }
JAVA

"$BT/aapt2$EXE" compile --dir res -o build/res.zip
"$BT/aapt2$EXE" link -I "$JAR" --manifest AndroidManifest.xml -o build/base.apk build/res.zip \
  --java build/gen --min-sdk-version 26 --target-sdk-version 33 \
  --version-code "$CODE" --version-name "$VERSION"

# -source 8 + anonymous classes: android.jar has no LambdaMetafactory for javac to target
javac -encoding UTF-8 -nowarn -Xlint:-options -source 8 -target 8 -bootclasspath "$JAR" -d build/classes \
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
SIGN=(sign --ks "$KS" --ks-pass "pass:$KS_PASS" --key-pass "pass:$KS_PASS" --out build/jellyxr-tv.apk)
[ -n "$ALIAS" ] && SIGN+=(--ks-key-alias "$ALIAS")
"$BT/apksigner$BAT" "${SIGN[@]}" build/aligned.apk
"$BT/apksigner$BAT" verify build/jellyxr-tv.apk
echo "built build/jellyxr-tv.apk version $VERSION ($CODE)"
