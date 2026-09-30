#!/usr/bin/env bash
# A radio over loopback (#104): a dedicated server that burns a fixture CD at boot (ffmpeg on
# PATH; no yt-dlp needed), a headless thrower that throws a radio, plays the CD and dances, and a
# windowed watcher (a headless client has no speaker) that must hear the CD on the shared clock
# and see the thrower dance.
#   tools/radiocheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/radiocheck.sh   (a worktree without terrain data)
set -u
PORT=7797
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
CHUNKARG=()
[ -n "${CHUNKS:-}" ] && CHUNKARG=(--chunks "$CHUNKS")
FIX="$PWD/$OUT/radiofixture.wav"
# 45 s of a 440 Hz tone with a beep every second: a 60 BPM click track the analyser can lock to
ffmpeg -y -loglevel error -f lavfi -i "sine=f=440:b=4:d=45" -ac 1 -ar 22050 "$FIX" || { echo "ffmpeg is needed on PATH"; exit 1; }
timeout 260 godot --headless --path . -- --server --port $PORT --cdfixture "$FIX" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_server.log 2>&1 &
# a full-world server blends its horizon for half a minute before it listens: wait for it
for i in $(seq 1 150); do
  grep -q "server listening" $OUT/radiocheck_server.log 2>/dev/null && break
  sleep 1
done
sleep 2
timeout 190 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Thrower --radiocheck thrower --traffic 0 --cache "$PWD/$OUT/radiocheck_cache_thrower" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_thrower.log 2>&1 &
sleep 2
timeout 185 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --radiocheck watch --traffic 0 --cache "$PWD/$OUT/radiocheck_cache_watch" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_watch.log 2>&1
code=$?
sleep 2
grep -h "\[radiocheck\]\|\[cd\]\|\[clock\]\|\[radio\]" $OUT/radiocheck_server.log $OUT/radiocheck_thrower.log $OUT/radiocheck_watch.log
kill %1 %2 2>/dev/null
exit $code
