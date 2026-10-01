#!/usr/bin/env bash
# Items dropped, thrown and picked up over loopback (#206): a dedicated server, a windowed thrower
# that drops a stone, winds up a full throw of another (screenshot of the arc at full charge:
# test_output/dropcheck_aim.png) and drops a stack of energy bars, and a windowed watcher that must see
# the wind-up pose and the three items settled (the thrown one far out), then point at the bars
# (screenshot of the outline: test_output/dropcheck_point.png) and pick them up.
#   tools/dropcheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/dropcheck.sh   (a worktree without terrain data)
set -u
PORT=7798
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
CHUNKARG=()
[ -n "${CHUNKS:-}" ] && CHUNKARG=(--chunks "$CHUNKS")
timeout 300 godot --headless --path . -- --server --port $PORT ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/dropcheck_server.log 2>&1 &
# a full-world server blends its horizon for half a minute before it listens: wait for it
for i in $(seq 1 150); do
  grep -q "server listening" $OUT/dropcheck_server.log 2>/dev/null && break
  sleep 1
done
sleep 2
timeout 200 godot --path . -- --connect 127.0.0.1:$PORT --name Thrower --dropcheck thrower --traffic 0 --cache "$PWD/$OUT/dropcheck_cache_thrower" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/dropcheck_thrower.log 2>&1 &
THROWER=$!
sleep 2
timeout 195 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --dropcheck watch --traffic 0 --cache "$PWD/$OUT/dropcheck_cache_watch" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/dropcheck_watch.log 2>&1
code=$?
wait $THROWER   # runs may exit 139 after their result: read the RESULT line
grep -q "RESULT: ok" $OUT/dropcheck_thrower.log || code=1
grep -q "RESULT: ok" $OUT/dropcheck_watch.log || code=1
grep -h "\[dropcheck\]" $OUT/dropcheck_thrower.log $OUT/dropcheck_watch.log
kill %1 2>/dev/null
exit $code
