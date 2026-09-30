#!/usr/bin/env bash
# Car switches over loopback (#48) and car presets (#40): a dedicated server and two clients. The
# driver logs in as admin (so the server lets it park the car it conjured), gets in the NA6CE, puts
# the top down and the lights on with the real key actions, fits the Rally-raid preset, then gets
# out; the watcher (windowed: a headless client draws no parked vehicles) must see all of it on the
# moving car and on the parked one, and saves test_output/switchcheck_watch.png.
#   tools/switchcheck.sh [E,N]      (default: the spawn; CHUNKS=<dir> for another terrain_chunks)
set -u
AT=${1:-}
PORT=7796
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
ATARG=()
[ -n "$AT" ] && ATARG=(--at "$AT")
CHARG=()
[ -n "${CHUNKS:-}" ] && CHARG=(--chunks "$CHUNKS")
C1=$(mktemp -d); C2=$(mktemp -d)
timeout 200 godot --headless --path . -- --server --port $PORT --admin-password switchcheck ${CHARG[@]+"${CHARG[@]}"} > $OUT/switchcheck_server.log 2>&1 &
sleep 6
timeout 190 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Driver --cache "$C1" --switchcheck driver switchcheck ${ATARG[@]+"${ATARG[@]}"} ${CHARG[@]+"${CHARG[@]}"} --traffic 0 > $OUT/switchcheck_driver.log 2>&1 &
sleep 2
timeout 185 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --cache "$C2" --switchcheck watch $OUT/switchcheck_watch.png ${ATARG[@]+"${ATARG[@]}"} ${CHARG[@]+"${CHARG[@]}"} --traffic 0 > $OUT/switchcheck_watch.log 2>&1
code=$?
sleep 2
grep -h "\[switchcheck\]" $OUT/switchcheck_driver.log $OUT/switchcheck_watch.log
kill %1 %2 2>/dev/null
rm -rf "$C1" "$C2"
exit $code
