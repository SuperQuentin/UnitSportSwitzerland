#!/usr/bin/env bash
# Car web radio over loopback (#179): a dedicated server (ffmpeg on PATH, internet) that tunes the
# stations, a headless driver that takes a car, tunes SRF 3, retunes to Radio Swiss Pop and parks,
# and a windowed watcher (a real speaker) that must hear each in sync with the clock. Both clients
# print a fingerprint of the audio due at every 5 s mark: they must agree on every common mark.
#   tools/webradiocheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/webradiocheck.sh   (a worktree without terrain data)
set -u
PORT=7798
PW=webradiopw
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
CHUNKARG=()
[ -n "${CHUNKS:-}" ] && CHUNKARG=(--chunks "$CHUNKS")
timeout 330 godot --headless --path . -- --server --port $PORT --admin-password $PW ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/webradio_server.log 2>&1 &
# a full-world server blends its horizon for half a minute before it listens: wait for it
for i in $(seq 1 150); do
  grep -q "server listening" $OUT/webradio_server.log 2>/dev/null && break
  sleep 1
done
sleep 2
timeout 230 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Driver --webradiocheck driver --webradiopw $PW --traffic 0 --cache "$PWD/$OUT/webradio_cache_driver" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/webradio_driver.log 2>&1 &
DRIVER=$!
sleep 2
timeout 225 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --webradiocheck watch --traffic 0 --cache "$PWD/$OUT/webradio_cache_watch" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/webradio_watch.log 2>&1
code=$?
wait $DRIVER   # headless runs may exit 139 after their result: read the RESULT line
grep -q "RESULT: ok" $OUT/webradio_driver.log || code=1
grep -q "RESULT: ok" $OUT/webradio_watch.log || code=1
grep -h "\[webradiocheck\]\|\[webradio\]\|\[clock\]" $OUT/webradio_server.log $OUT/webradio_driver.log $OUT/webradio_watch.log | grep -v " fp "
# the same audio at the same moment on both clients
fp() { grep -o "fp mark [0-9]* station [0-9]* sum [0-9.]*" "$1" | sort -u; }
common=$(join -j1 <(fp $OUT/webradio_driver.log | awk '{print $3"/"$5, $7}' | sort) <(fp $OUT/webradio_watch.log | awk '{print $3"/"$5, $7}' | sort))
total=$(echo "$common" | grep -c . )
same=$(echo "$common" | awk '$2==$3' | grep -c .)
echo "[webradiocheck] fingerprints: $same of $total common marks identical on both clients"
[ "$total" -gt 5 ] && [ "$same" -eq "$total" ] || code=1
kill %1 2>/dev/null
echo "[webradiocheck] exit $code"
exit $code
