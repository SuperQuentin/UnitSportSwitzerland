#!/usr/bin/env bash
# Shared birds over loopback (src/Birds/BirdNetProbe, #143): a dedicated server (--generated-world) and two
# headless clients. Both must be sent the same birds by the server; A shoots one through the real item path
# (killed on the server, scored in A's journal only, falling on B's screen too); A's air shot flushes the
# birds for B. Output in test_output/birdnet_*.log.  GODOT=<exe> [SERVER_ARGS=] tools/birdnetcheck.sh [E,N]
# WARNING: the clients write the real user://birds.json of this machine (a kill scores in A's journal).
set -u
AT=${1:-2583250,1113250}
PORT=7795
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
# SERVER_ARGS= (empty) serves the real terrain of UNITSPORT_CHUNKS instead of the generated world
timeout 300 "$GODOT" --headless --path . -- --server ${SERVER_ARGS---generated-world} --port $PORT > "$OUT/birdnet_server.log" 2>&1 & SERVER=$!
sleep 12
client() { timeout 240 "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name "Bird$1" --cache "$OUT/birdnet_cache_$1" \
    --at "$AT" --view first --birdnetcheck "$1" > "$OUT/birdnet_$1.log" 2>&1; }
client A & A=$!
client B & B=$!
wait $A $B
# a headless Godot may ignore SIGTERM (and may exit 139 after its result): kill hard, read RESULT lines
kill -9 $SERVER 2>/dev/null; pkill -9 -f -- "--port $PORT" 2>/dev/null
grep -h "\[birdnet\|\[birds\] killed" "$OUT/birdnet_A.log" "$OUT/birdnet_B.log"
if [ "$(grep -h "RESULT: ok" "$OUT/birdnet_A.log" "$OUT/birdnet_B.log" | wc -l)" -eq 2 ]; then echo "[birdnetcheck] RESULT: ok"; exit 0; fi
echo "[birdnetcheck] RESULT: FAILED (see $OUT/birdnet_*.log)"; exit 1
