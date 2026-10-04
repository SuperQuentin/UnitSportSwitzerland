#!/usr/bin/env bash
# The AN-124's doors and kneeling over loopback (#419, src/Player/An124NetProbe): a dedicated server on
# the flat fixture with an admin password and two headless clients. A (admin) takes an AN-124, opens
# everything and kneels it with G: B's copy shows the doors and the frame down; A gets out, it stands
# parked open and knelt; B walks up the nose ramp, raises it with the kneeling button inside (A sees it
# rise) and shuts the visor by its button (A sees it shut). Output in test_output/an124net_*.log.
#   GODOT=<exe> [PORT=] tools/an124netcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" an124net
PORT=${PORT:-7873}
WORLD="--world fixture --chunks fixture:flat"
tc_server 360 120 "$OUT/an124net_server.log" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/an124net_$1.log" --connect 127.0.0.1:$PORT --name "Loadmaster$1" $WORLD --an124net "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[an124net" "$OUT/an124net_A.log" "$OUT/an124net_B.log"
if tc_ok 2 "$OUT/an124net_A.log" "$OUT/an124net_B.log"; then echo "[an124netcheck] RESULT: ok"; exit 0; fi
echo "[an124netcheck] RESULT: FAILED (see $OUT/an124net_*.log)"; exit 1
