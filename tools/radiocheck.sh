#!/usr/bin/env bash
# A radio over loopback (#104, #168): a dedicated server that burns two fixture CDs at boot
# (ffmpeg on PATH; no yt-dlp needed), a headless thrower that throws a radio, plays CD A, dances,
# changes to CD B, picks the radio up, plays A in the hand, then burns and plays a personal CD; and
# a windowed watcher (a headless client has no speaker) that must hear each of those on the shared
# clock from the right file, and nothing for the personal CD.
#   tools/radiocheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/radiocheck.sh   (a worktree without terrain data)
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
PORT=7797
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
CHUNKARG=()
[ -n "${CHUNKS:-}" ] && CHUNKARG=(--chunks "$CHUNKS")
FIX="$PWD/$OUT/radiofixture.wav"
FIX2="$PWD/$OUT/radiofixture2.wav"
MINE="$PWD/$OUT/radiopersonal.wav"
# tones with a beep every second (60 BPM click tracks the analyser can lock to), of different
# lengths so the watcher can tell which file a speaker loaded
ffmpeg -y -loglevel error -f lavfi -i "sine=f=440:b=4:d=45" -ac 1 -ar 22050 "$FIX" || { echo "ffmpeg is needed on PATH"; exit 1; }
ffmpeg -y -loglevel error -f lavfi -i "sine=f=660:b=4:d=50" -ac 1 -ar 22050 "$FIX2"
ffmpeg -y -loglevel error -f lavfi -i "sine=f=330:b=4:d=20" -ac 1 -ar 22050 "$MINE"
timeout 330 godot --headless --path . -- --server --port $PORT --cdfixture "$FIX" --cdfixture "$FIX2" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_server.log 2>&1 &
# a full-world server blends its horizon for half a minute before it listens: wait for it
for i in $(seq 1 150); do
  grep -q "server listening" $OUT/radiocheck_server.log 2>/dev/null && break
  sleep 1
done
sleep 2
timeout 230 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Thrower --radiocheck thrower --radiopersonal "$MINE" --traffic 0 --cache "$PWD/$OUT/radiocheck_cache_thrower" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_thrower.log 2>&1 &
THROWER=$!
sleep 2
timeout 225 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --radiocheck watch --traffic 0 --cache "$PWD/$OUT/radiocheck_cache_watch" ${CHUNKARG[@]+"${CHUNKARG[@]}"} > $OUT/radiocheck_watch.log 2>&1
code=$?
# the thrower finishes its own steps (and deletes its personal fixture CD) a little after
wait $THROWER   # headless runs may exit 139 after their result: read the RESULT line
grep -q "RESULT: ok" $OUT/radiocheck_thrower.log || code=1
grep -h "\[radiocheck\]\|\[cd\]\|\[clock\]\|\[radio\]" $OUT/radiocheck_server.log $OUT/radiocheck_thrower.log $OUT/radiocheck_watch.log
kill %1 2>/dev/null
exit $code
