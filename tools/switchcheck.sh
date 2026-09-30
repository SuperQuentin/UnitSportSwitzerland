#!/usr/bin/env bash
# Car switches over loopback (#48): a dedicated server and two clients. The driver gets in the
# NA6CE, puts the top down and the lights on with the real key actions, then gets out; the watcher
# (windowed: a headless client draws no parked vehicles) must see both on the moving car and on
# the parked one.
#   tools/switchcheck.sh [E,N]      (default: the spawn)
set -u
AT=${1:-}
PORT=7796
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
ATARG=()
[ -n "$AT" ] && ATARG=(--at "$AT")
timeout 200 godot --headless --path . -- --server --port $PORT > $OUT/switchcheck_server.log 2>&1 &
sleep 6
timeout 190 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Driver --switchcheck driver ${ATARG[@]+"${ATARG[@]}"} --traffic 0 > $OUT/switchcheck_driver.log 2>&1 &
sleep 2
timeout 185 godot --path . -- --connect 127.0.0.1:$PORT --name Watcher --switchcheck watch ${ATARG[@]+"${ATARG[@]}"} --traffic 0 > $OUT/switchcheck_watch.log 2>&1
code=$?
sleep 2
grep -h "\[switchcheck\]" $OUT/switchcheck_driver.log $OUT/switchcheck_watch.log
kill %1 %2 2>/dev/null
exit $code
