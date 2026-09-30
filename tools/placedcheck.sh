#!/usr/bin/env bash
# Held-item events and placed objects over loopback (src/Items/PlacedProbe):
#   1. dedicated server (--generated-world); client A plants a flag (real item path), sticks a photo card,
#      takes a Polaroid and sticks it on the ground (real item path: its JPEG is uploaded to the server)
#   2. client B joins only AFTER that: it must get both from the join snapshot, hear A's shots and
#      camera flash, and be refused removing A's photo; screenshot test_output/placedcheck_b.png. It must
#      fetch the Polaroid's image by hash from the server (own --photo-dir: no shared disk) and draw it:
#      test_output/placedcheck_b_photo.png. A then takes its photos back.
#   3. the server is restarted; client C must find A's flag still there, then pulls it up (cleanup)
#   GODOT=<exe> tools/placedcheck.sh [E,N]
# WARNING: the server keeps its list in the real user://placed/server.json of this project (and the
# uploaded Polaroid in user://placed/photos/).
set -u
AT=${1:-2583250,1113250}
PORT=7793
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
# each client its own Polaroid archive and fetched-photo cache (absolute: Godot resolves it, not bash)
PHOTOS="$(pwd -W 2>/dev/null || pwd)/$OUT"
rm -rf "$OUT"/placed_photos_*
server() { timeout 400 "$GODOT" --headless --path . -- --server --generated-world --port $PORT > "$OUT/placed_server$1.log" 2>&1 & SERVER=$!; }
client() { timeout 240 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name "Placed$1" --cache "$OUT/placed_cache_$1" \
    --photo-dir "$PHOTOS/placed_photos_$1" --at "$AT" --placedcheck "$1" "${@:2}" > "$OUT/placed_$1.log" 2>&1; }

server 1
sleep 12
client A & A=$!
for _ in $(seq 1 180); do grep -q "A\] say planted" "$OUT/placed_A.log" 2>/dev/null && break; sleep 1; done
client B --view first   # first person: the Polaroid on the ground fills the screenshot
wait $A
kill $SERVER 2>/dev/null; wait $SERVER 2>/dev/null
sleep 2
server 2
sleep 12
client C
kill $SERVER 2>/dev/null
grep -h "\[placedcheck\|\[items\] event\|\[photo\]" "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log
grep -h "\[placed\]\|\[photo\]" "$OUT"/placed_server*.log
if [ "$(grep -h "RESULT: ok" "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log | wc -l)" -eq 3 ]; then
    echo "[placedcheck] RESULT: ok"; exit 0
fi
echo "[placedcheck] RESULT: FAILED (see $OUT/placed_*.log)"; exit 1
