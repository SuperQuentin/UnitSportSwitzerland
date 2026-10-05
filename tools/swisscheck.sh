#!/usr/bin/env bash
# The Swiss match items over loopback (#478, src/Items/SwissNetProbe), in free roam on the flat fixture: B stands
# 2 m from A, both hurt. A's fondue feeds both (+40), A's smoke canister lands on B (B is in the cloud on its own
# peer), A's alphorn is heard by B. Output in test_output/swiss_*.log; SHOTS=1 saves B's view of the smoke.
#   GODOT=<exe> [PORT=] tools/swisscheck.sh
. "$(dirname "$0")/lib/twoclient.sh" swiss
PORT=${PORT:-7875}
WORLD="--world fixture --chunks fixture:flat"
tc_server 240 120 "$OUT/swiss_server.log" --server --port $PORT $WORLD
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 200 "$OUT/swiss_A.log" --connect 127.0.0.1:$PORT --name SwissA $WORLD --swissnet A & A=$!
tc_client 200 "$OUT/swiss_B.log" $WIN --connect 127.0.0.1:$PORT --name SwissB $WORLD --view third --swissnet B & B=$!
wait $A $B
tc_stop
grep -h "\[swiss" "$OUT/swiss_A.log" "$OUT/swiss_B.log"
if tc_ok 2 "$OUT/swiss_A.log" "$OUT/swiss_B.log"; then echo "[swisscheck] RESULT: ok"; exit 0; fi
echo "[swisscheck] RESULT: FAILED (see $OUT/swiss_*.log)"; exit 1
