#!/usr/bin/env bash
# Fishing over loopback (#493, src/Items/Fishing/FishNetProbe) on the lake fixture: A casts from the beach, B a few
# metres behind sees A's rod, draws A's float where A's lies, and loses it when A winds in and when A puts the rod
# away. Output in test_output/fish_*.log.
#   GODOT=<exe> [PORT=] tools/fishcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" fish
PORT=${PORT:-7876}
WORLD="--world fixture --chunks fixture:lake"
tc_server 300 150 "$OUT/fish_server.log" --server --port $PORT $WORLD
tc_client 260 "$OUT/fish_A.log" --connect 127.0.0.1:$PORT --name FishA $WORLD --fishnet A & A=$!
tc_client 260 "$OUT/fish_B.log" --connect 127.0.0.1:$PORT --name FishB $WORLD --fishnet B & B=$!
wait $A $B
tc_stop
grep -h "\[fishnet" "$OUT/fish_A.log" "$OUT/fish_B.log"
if tc_ok 2 "$OUT/fish_A.log" "$OUT/fish_B.log"; then echo "[fishcheck] RESULT: ok"; exit 0; fi
echo "[fishcheck] RESULT: FAILED (see $OUT/fish_*.log)"; exit 1
