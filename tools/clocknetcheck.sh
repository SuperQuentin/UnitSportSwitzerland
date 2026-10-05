#!/usr/bin/env bash
# The time of day over loopback (#452, src/World/ClockNetProbe): a dedicated server on the flat fixture
# with an admin password, client A, then client B some seconds later with its own clock stopped at 03:00
# (--time 3). Both skies must settle on the server's world clock and agree at the same server instant;
# A's /time set 22 and /time speed 2 must reach both. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/clocknetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" clocknet
PORT=${PORT:-7873}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/clocknet_server.log" --server --port $PORT $WORLD --admin-password test
tc_client 240 "$OUT/clocknet_A.log" --connect 127.0.0.1:$PORT --name ClockA $WORLD --clocknet A & A=$!
sleep 15   # B joins later: before #452 it would have kept its own, later, day
tc_client 220 "$OUT/clocknet_B.log" --connect 127.0.0.1:$PORT --name ClockB $WORLD --time 3 --clocknet B & B=$!
wait $A $B
tc_stop
grep -h "\[clocknet\|\[time\]" "$OUT/clocknet_A.log" "$OUT/clocknet_B.log" "$OUT/clocknet_server.log"
if tc_ok 2 "$OUT/clocknet_A.log" "$OUT/clocknet_B.log"; then echo "[clocknetcheck] RESULT: ok"; exit 0; fi
echo "[clocknetcheck] RESULT: FAILED (see $OUT/clocknet_*.log)"; exit 1
