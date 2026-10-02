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
# The server keeps its list in user://placed/server.json (and the uploaded Polaroid in user://placed/photos/):
# test_output/userdata_placed, kept across the restart (lib/twoclient.sh).
. "$(dirname "$0")/lib/twoclient.sh" placed
AT=${1:-2583250,1113250}
PORT=7793
# each client its own Polaroid archive and fetched-photo cache (absolute: Godot resolves it, not bash)
PHOTOS="$(pwd -W 2>/dev/null || pwd)/$OUT"
find "$OUT" -maxdepth 1 -name 'placed_photos_*' -exec rm -r {} +
server() { tc_server 400 120 "$OUT/placed_server$1.log" --server --generated-world --port $PORT; }
client() { tc_client 240 "$OUT/placed_$1.log" --windowed --connect 127.0.0.1:$PORT --name "Placed$1" --cache "$OUT/placed_cache_$1" \
    --photo-dir "$PHOTOS/placed_photos_$1" --at "$AT" --placedcheck "$1" "${@:2}"; }

server 1
: > "$OUT/placed_A.log"   # emptied first: the wait below must not read the last run's log
client A & A=$!
for _ in $(seq 1 180); do grep -q "A\] say planted" "$OUT/placed_A.log" 2>/dev/null && break; sleep 1; done
client B --view first   # first person: the Polaroid on the ground fills the screenshot
wait $A
tc_stop
sleep 2
server 2
client C
tc_stop
grep -h "\[placedcheck\|\[items\] event\|\[photo\]" "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log
grep -h "\[placed\]\|\[photo\]" "$OUT"/placed_server*.log
if tc_ok 3 "$OUT"/placed_A.log "$OUT"/placed_B.log "$OUT"/placed_C.log; then
    echo "[placedcheck] RESULT: ok"; exit 0
fi
echo "[placedcheck] RESULT: FAILED (see $OUT/placed_*.log)"; exit 1
