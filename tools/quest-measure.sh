#!/usr/bin/env bash
# One frame-time run on a connected Quest (#1112): tools/quest-measure.sh <label> <seconds> <game args...>
# The APK from tools/quest.sh must be installed. Writes the game's arguments to user://cmdline.txt (Core/CmdArgs reads
# it on Android), keeps the headset "worn" and awake, starts the game, and after <seconds> prints the Quest's VrApi
# averages over the run's last 40 s (fps, app GPU time, GPU and CPU use). Startup takes about 90 s: 150 is a good length.
# The logs land in test_output/quest/runs/<label>.{log,vrapi}. Notes: docs/notes/xr/quest-standalone.md.
# Example: tools/quest-measure.sh street 150 --chunks terrain_chunks --at 2500300,1118450 --heading 207 --seat kart
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ADB=${ADB:-$(command -v adb || echo "${LOCALAPPDATA:-}/Android/Sdk/platform-tools/adb.exe")}
PKG=ch.unitsport.switzerland.quest
[ $# -ge 2 ] || { echo "Usage: tools/quest-measure.sh <label> <seconds> <game args...>"; exit 2; }
LABEL=$1; SECS=$2; shift 2
OUT=test_output/quest/runs; mkdir -p "$OUT"
"$ADB" shell am force-stop $PKG
echo "$*" | "$ADB" shell run-as $PKG sh -c "'cat > files/cmdline.txt'"
"$ADB" shell run-as $PKG rm -f files/logs/godot.log
# the proximity sensor reads "worn" (until automation_enable), the Guardian does not pause the app, the screen wakes
"$ADB" shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
"$ADB" shell setprop debug.oculus.guardian_pause 1
"$ADB" shell input keyevent KEYCODE_WAKEUP
"$ADB" logcat -c
"$ADB" shell am start -n $PKG/com.godot.game.GodotAppLauncher >/dev/null
sleep "$SECS"
# logcat, not the game's own log file: Android buffers that one until the app stops
"$ADB" logcat -d -s VrApi:I > "$OUT/$LABEL.vrapi"
"$ADB" logcat -d -s godot:I > "$OUT/$LABEL.log"
echo "== $LABEL ($*)"
grep -E "\[xr\] load|\[world\] ready|\[seat\]" "$OUT/$LABEL.log" | tail -4
grep "FPS=" "$OUT/$LABEL.vrapi" | tail -n 40 | sed -E 's/.*FPS=([0-9]+)\/.*App=([0-9.]+)ms.*GPU%=([-0-9.]+),CPU%=([0-9.]+).*/\1 \2 \3 \4/' \
  | awk '{f+=$1; a+=$2; g+=$3; c+=$4; k++} END {if (k) printf "vrapi: fps %.1f  app gpu %.1f ms  gpu%% %.2f  cpu%% %.2f  (%d s)\n", f/k, a/k, g/k, c/k, k}'
