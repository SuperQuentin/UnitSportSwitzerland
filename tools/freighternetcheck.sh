#!/usr/bin/env bash
# The military freighter's doors over loopback (#420, src/Player/FreighterNetProbe): a dedicated server on
# the flat fixture with an admin password and two headless clients. A (admin) takes a freighter and lowers
# the ramp with G: B's copy shows it; A gets out, it stands parked with the ramp down; B walks up the ramp
# into the hold and shuts it with the button inside; A sees it shut. Output in test_output/freighternet_*.log.
#   GODOT=<exe> [PORT=] tools/freighternetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" freighternet
PORT=${PORT:-7863}
WORLD="--world fixture --chunks fixture:flat"
tc_server 360 120 "$OUT/freighternet_server.log" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/freighternet_$1.log" --connect 127.0.0.1:$PORT --name "Loadmaster$1" $WORLD --freighternet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[freighternet" "$OUT/freighternet_A.log" "$OUT/freighternet_B.log"
if tc_ok 2 "$OUT/freighternet_A.log" "$OUT/freighternet_B.log"; then echo "[freighternetcheck] RESULT: ok"; exit 0; fi
echo "[freighternetcheck] RESULT: FAILED (see $OUT/freighternet_*.log)"; exit 1
