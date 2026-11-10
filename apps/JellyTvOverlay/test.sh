#!/usr/bin/env bash
# JVM unit tests for the pure model/logic classes (no android.* imports allowed there).
# Downloads junit console + org.json into build/testlib on first run.
set -euo pipefail
cd "$(dirname "$0")"

LIB=build/testlib
MVN=https://repo1.maven.org/maven2
JUNIT=junit-platform-console-standalone-1.11.3.jar
JSON=json-20240303.jar
mkdir -p "$LIB"
[ -s "$LIB/$JUNIT" ] || curl -fsSL -o "$LIB/$JUNIT" "$MVN/org/junit/platform/junit-platform-console-standalone/1.11.3/$JUNIT"
[ -s "$LIB/$JSON" ]  || curl -fsSL -o "$LIB/$JSON"  "$MVN/org/json/json/20240303/$JSON"

# guard: model/logic must stay free of Android
if grep -rl 'import android\.' $(find src -type d \( -name model -o -name logic \)) 2>/dev/null; then
  echo "android.* import in model/logic" >&2; exit 1
fi

OUT=build/testclasses
rm -rf "$OUT" && mkdir -p "$OUT"
SRC=$(find src -path '*/model/*.java' -o -path '*/logic/*.java'; find test -name '*.java')
CP="$LIB/$JUNIT$([ "$(uname -o 2>/dev/null)" = Msys ] && echo ';' || echo ':')$LIB/$JSON"
javac -encoding UTF-8 -nowarn -Xlint:-options -source 8 -target 8 -cp "$CP" -d "$OUT" $SRC
java -jar "$LIB/$JUNIT" execute --class-path "$OUT$([ "$(uname -o 2>/dev/null)" = Msys ] && echo ';' || echo ':')$LIB/$JSON" \
  --scan-class-path --disable-banner --details=tree --disable-ansi-colors
