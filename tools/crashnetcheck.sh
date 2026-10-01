#!/usr/bin/env bash
# The crash ragdoll over loopback (#214): a dedicated server, a headless driver (a) that takes a car
# and drives into a wall until it is thrown through the windscreen, and a windowed watcher (b) that
# must see a's copy go limp, follow a's replicated position and get up when a does.
#   tools/crashnetcheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/crashnetcheck.sh   (a worktree without terrain data)
set -u
PORT=7814
PW=crashcheck
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
CHUNKARG=()
[ -n "${CHUNKS:-}" ] && CHUNKARG=(--chunks "$CHUNKS")
timeout 260 godot --headless --path . -- --server --port $PORT --admin-password $PW ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/crashnet_server.log 2>&1 &
# a full-world server blends its horizon for half a minute before it listens: wait for it
for i in $(seq 1 150); do
  grep -q "server listening" $OUT/crashnet_server.log 2>/dev/null && break
  sleep 1
done
sleep 2
timeout 200 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --crashnet b --traffic 0 --cache "$PWD/$OUT/crashnet_cache_b" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/crashnet_b.log 2>&1 &
WATCHER=$!
sleep 3
timeout 190 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Driver --crashnet a $PW --traffic 0 --cache "$PWD/$OUT/crashnet_cache_a" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/crashnet_a.log 2>&1
wait $WATCHER   # headless runs may exit 139 after their result: read the RESULT lines
code=0
grep -q "a RESULT: ok" $OUT/crashnet_a.log || code=1
grep -q "b RESULT: ok" $OUT/crashnet_b.log || code=1
grep -h "\[crashnet\]" $OUT/crashnet_a.log $OUT/crashnet_b.log
kill %1 2>/dev/null
exit $code
