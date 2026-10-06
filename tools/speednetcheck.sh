#!/usr/bin/env bash
# The simulation's pace over loopback (#579, src/Core/SpeedNetProbe): a dedicated server on the flat
# fixture with an admin password, plus clients A and B. A's /speed 0.25 must reach both peers' clock
# and engine, both must still agree about simulated time at the same server instant, and /speed
# normal must bring both back. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/speednetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" speednet
PORT=${PORT:-7875}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/speednet_server.log" --server --port $PORT $WORLD --admin-password test
tc_client 240 "$OUT/speednet_A.log" --connect 127.0.0.1:$PORT --name SpeedA $WORLD --speednet A & A=$!
sleep 8
tc_client 220 "$OUT/speednet_B.log" --connect 127.0.0.1:$PORT --name SpeedB $WORLD --speednet B & B=$!
wait $A $B
tc_stop
grep -h "\[speednet\|\[speed\]" "$OUT/speednet_A.log" "$OUT/speednet_B.log" "$OUT/speednet_server.log"
if tc_ok 2 "$OUT/speednet_A.log" "$OUT/speednet_B.log"; then echo "[speednetcheck] RESULT: ok"; exit 0; fi
echo "[speednetcheck] RESULT: FAILED (see $OUT/speednet_*.log)"; exit 1
