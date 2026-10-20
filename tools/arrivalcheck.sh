#!/usr/bin/env bash
# The server decides where players start and whether they may teleport (#799, src/Net/Arrival +
# ArrivalProbe): a dedicated server on the flat fixture with --spawn 150 m east of Riddes and
# --no-teleport; one client joins with no --at and must start there, know it may not teleport, be
# refused /city and /stadium <venue> (the venue list and the nearest one still answered, #994), and come back from the fly camera where it stood. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/arrivalcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" arrival
PORT=${PORT:-7879}
WORLD="--world fixture --chunks fixture:flat"
SPAWN=2583400,1113300
tc_server 400 120 "$OUT/arrival_server.log" --server --port $PORT $WORLD --spawn $SPAWN --no-teleport
tc_client 240 "$OUT/arrival_A.log" --connect 127.0.0.1:$PORT --name Arriver $WORLD --arrival check --expect-spawn $SPAWN
tc_stop
grep -h "\[arrival" "$OUT/arrival_server.log" "$OUT/arrival_A.log"
if tc_ok 1 "$OUT/arrival_A.log"; then echo "[arrivalcheck] RESULT: ok"; exit 0; fi
echo "[arrivalcheck] RESULT: FAILED (see $OUT/arrival_*.log)"; exit 1
