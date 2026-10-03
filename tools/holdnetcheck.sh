#!/usr/bin/env bash
# A car carried in another player's aircraft over loopback (#418, src/Player/HoldNetProbe): a dedicated
# server on the flat fixture with an admin password and two headless clients, both giving the A320 the
# checks' test hold. B drives a car up into A's hold and ties it down; A taxis, flies and lands; B parks
# it in the hold, A taxis again, B gets back in and reverses out. Both peers check B's car never leaves
# its spot in A's frame and agree where it ends up. No terrain data needed. Output in test_output/holdnet_*.log.
#   GODOT=<exe> [PORT=] tools/holdnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" holdnet
PORT=${PORT:-7871}
WORLD="--world fixture --chunks fixture:flat --traffic 0"
tc_server 420 120 "$OUT/holdnet_server.log" --server --port $PORT $WORLD --admin-password test
client() { tc_client 360 "$OUT/holdnet_$1.log" --connect 127.0.0.1:$PORT --name "Hold$1" $WORLD --holdnet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[holdnet" "$OUT/holdnet_A.log" "$OUT/holdnet_B.log"
if tc_ok 2 "$OUT/holdnet_A.log" "$OUT/holdnet_B.log"; then echo "[holdnetcheck] RESULT: ok"; exit 0; fi
echo "[holdnetcheck] RESULT: FAILED (see $OUT/holdnet_*.log)"; exit 1
