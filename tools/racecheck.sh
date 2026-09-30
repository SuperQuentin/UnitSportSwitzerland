#!/usr/bin/env bash
# Multiplayer race over loopback: a dedicated server and two clients, each driven by AutoPilot.
# Client A opens a race (/race start), client B joins it (/race join); both race to the finish.
# Passes when the server announces results with both drivers classified.
#   tools/racecheck.sh [E,N] [metres]      (default: Col du Mollendruz, 1500 m)
set -u
AT=${1:-2518038,1167321}
M=${2:-1500}
PORT=7795
OUT=test_output
mkdir -p "$OUT"
cd "$(dirname "$0")/.."
timeout 300 godot --headless --path . -- --server --port $PORT > $OUT/racecheck_server.log 2>&1 &
sleep 6
timeout 290 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Takumi --raceauto --racestart "$M" --at "$AT" --traffic 0 > $OUT/racecheck_a.log 2>&1 &
sleep 2
timeout 285 godot --headless --path . -- --connect 127.0.0.1:$PORT --name Keisuke --raceauto --racejoin --at "$AT" --traffic 0 > $OUT/racecheck_b.log 2>&1
sleep 3
grep -h "\[race\] results" $OUT/racecheck_server.log
if grep -q "\[race\] results: 1\..*2\." $OUT/racecheck_server.log; then echo "[racecheck] RESULT: ok"; exit 0; fi
echo "[racecheck] RESULT: FAILED (see $OUT/racecheck_*.log)"; exit 1
