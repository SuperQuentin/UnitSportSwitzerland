#!/usr/bin/env bash
# Held-item events and placed objects over loopback (src/Items/PlacedProbe):
#   1. dedicated server (--generated-world); client A plants a flag (real item path), sticks a photo card
#   2. client B joins only AFTER that: it must get both from the join snapshot, hear A's shots and
#      camera flash, and be refused removing A's photo; screenshot test_output/placedcheck_b.png
#   3. the server is restarted; client C must find A's flag still there, then pulls it up (cleanup)
#   GODOT=<exe> tools/placedcheck.sh [E,N]
# WARNING: the server keeps its list in the real user://placed/server.json of this project.
set -u
AT=${1:-2583250,1113250}
PORT=7793
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
server() { timeout 400 "$GODOT" --headless --path . -- --server --generated-world --port $PORT > "$OUT/placed_server$1.log" 2>&1 & SERVER=$!; }
client() { timeout 240 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name "Placed$1" --cache "$OUT/placed_cache_$1" \
    --at "$AT" --placedcheck "$1" > "$OUT/placed_$1.log" 2>&1; }

server 1
sleep 12
client A & A=$!
for _ in $(seq 1 180); do grep -q "A\] say planted" "$OUT/placed_A.log" 2>/dev/null && break; sleep 1; done
client B
wait $A
kill $SERVER 2>/dev/null; wait $SERVER 2>/dev/null
sleep 2
server 2
sleep 12
client C
kill $SERVER 2>/dev/null
grep -h "\[placedcheck\|\[items\] event" "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log
grep -h "\[placed\]" "$OUT"/placed_server*.log
if [ "$(grep -h "RESULT: ok" "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log | wc -l)" -eq 3 ]; then
    echo "[placedcheck] RESULT: ok"; exit 0
fi
echo "[placedcheck] RESULT: FAILED (see $OUT/placed_*.log)"; exit 1
