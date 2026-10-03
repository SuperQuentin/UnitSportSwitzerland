#!/usr/bin/env bash
# The A320 over loopback (#414, src/Player/AirlinerNetProbe): a dedicated server on the flat fixture with an
# admin password and two headless clients. A (admin) flies an A320: B sees its flaps, speedbrake, stick and
# spool, then the gear folding up on its copy; A parks it with flaps 2 and the parking brake, and B gets in
# and finds them so. No terrain data needed. Output in test_output/airlinernet_*.log.
#   GODOT=<exe> [PORT=] tools/airlinernetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" airlinernet
PORT=${PORT:-7861}
WORLD="--world fixture --chunks fixture:flat"
tc_server 360 120 "$OUT/airlinernet_server.log" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/airlinernet_$1.log" --connect 127.0.0.1:$PORT --name "Pilot$1" $WORLD --airlinernet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[airlinernet" "$OUT/airlinernet_A.log" "$OUT/airlinernet_B.log"
if tc_ok 2 "$OUT/airlinernet_A.log" "$OUT/airlinernet_B.log"; then echo "[airlinernetcheck] RESULT: ok"; exit 0; fi
echo "[airlinernetcheck] RESULT: FAILED (see $OUT/airlinernet_*.log)"; exit 1
