#!/usr/bin/env bash
# The flag ghost, plant and pull-up over loopback (src/Items/PlantProbe): dedicated server (--generated-world),
# client A holds the flag (ghost valid / red, plants, pulls up: test_output/plant_a_*.png), client B screenshots
# the body of A and the spawned flag in frames after the "go" of A (test_output/plant_b_NN.png).
#   GODOT=<exe> tools/plantcheck.sh
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
AT=2583250,1113250
PORT=7796
GODOT=${GODOT:-godot}
# the server and the Godot under its timeout wrapper, by PID (on Git Bash a kill stops only the wrapper)
stop() { for C in $(ps -ef | awk -v p=$1 '$3 == p { print $2 }'); do kill -9 $C 2>/dev/null; done; kill -9 $1 2>/dev/null; }
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 300 "$GODOT" --headless --path . -- --server --generated-world --traffic 0 --port $PORT > "$OUT/plant_server.log" 2>&1 & SERVER=$!
trap 'stop $SERVER' EXIT
sleep 12
client() { timeout 200 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name "Plant$1" --cache "$OUT/plant_cache_$1" \
    --at "$AT" --view first --traffic 0 --plantcheck "$1" "${@:2}" > "$OUT/plant_$1.log" 2>&1; }
client A --hold SwissFlag & A=$!
client B
wait $A
stop $SERVER
grep -h "\[plantcheck" "$OUT"/plant_A.log "$OUT"/plant_B.log
if [ "$(grep -h "RESULT: ok" "$OUT"/plant_A.log "$OUT"/plant_B.log | wc -l)" -eq 2 ]; then
    echo "[plantcheck] RESULT: ok"; exit 0
fi
echo "[plantcheck] RESULT: FAILED (see $OUT/plant_*.log)"; exit 1
