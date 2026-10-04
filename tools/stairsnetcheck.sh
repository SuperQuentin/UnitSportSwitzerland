#!/usr/bin/env bash
# Riding airstairs another player drives, over loopback (#445, src/Player/StairsNetProbe): a dedicated
# server on the flat fixture with an admin password and two headless clients. A parks an A320 with L1
# and L2 open and drives airstairs at L2; B stands on the platform, is carried as they dock and rise,
# both peers agree where B stands and how high the platform is; B walks into the cabin, back out and
# down to the ground. No terrain data needed. Output in test_output/stairsnet_*.log.
#   GODOT=<exe> [PORT=] tools/stairsnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" stairsnet
PORT=${PORT:-7863}
WORLD="--world fixture --chunks fixture:flat --traffic 0 --airliner arcade"
tc_server 400 120 "$OUT/stairsnet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test
client() { tc_client 340 "$OUT/stairsnet_$1.log" --connect 127.0.0.1:$PORT --name "Stairs$1" $WORLD --stairsnet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[stairsnet" "$OUT/stairsnet_A.log" "$OUT/stairsnet_B.log"
if tc_ok 2 "$OUT/stairsnet_A.log" "$OUT/stairsnet_B.log"; then echo "[stairsnetcheck] RESULT: ok"; exit 0; fi
echo "[stairsnetcheck] RESULT: FAILED (see $OUT/stairsnet_*.log)"; exit 1
