#!/usr/bin/env bash
# The shotgun's feel over loopback (src/Items/ShotgunProbe): dedicated server (--generated-world), client A holds the
# shotgun aimed and fires (screenshots of its own view), client B stands nearby and screenshots A's body:
#   test_output/gunshot_{a_aimed,a_kick,a_pump,b_kick,b_pump,b_after}.png
#   GODOT=<exe> tools/gunshotcheck.sh [--gunside]   (side: B looks along A's shoulder line; files gunshot_s_*.png)
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
AT=2583250,1113250
PORT=7795
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 300 "$GODOT" --headless --path . -- --server --generated-world --port $PORT > "$OUT/gunshot_server.log" 2>&1 & SERVER=$!
trap 'kill $SERVER 2>/dev/null' EXIT
sleep 12
client() { timeout 200 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name "Gun$1" --cache "$OUT/gunshot_cache_$1" \
    --at "$AT" --view first --gunshot "$1" "${@:2}" > "$OUT/gunshot_$1.log" 2>&1; }
client A --hold Shotgun --aim & A=$!
client B "$@"
wait $A
kill $SERVER 2>/dev/null
grep -h "\[gunshot" "$OUT"/gunshot_A.log "$OUT"/gunshot_B.log
if [ "$(grep -h "RESULT: ok" "$OUT"/gunshot_A.log "$OUT"/gunshot_B.log | wc -l)" -eq 2 ]; then
    echo "[gunshotcheck] RESULT: ok"; exit 0
fi
echo "[gunshotcheck] RESULT: FAILED (see $OUT/gunshot_*.log)"; exit 1
