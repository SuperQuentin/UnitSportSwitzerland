#!/usr/bin/env bash
# The farm machines from another peer, over loopback (#494, src/Player/TractorNetProbe): a dedicated
# server on the flat fixture with an admin password and two headless clients. A takes the tractor,
# lowers its plough and parks it; B sees the plough down on A's tractor and on the parked one. A
# takes a tipping trailer with 77 sacks of wheat and parks the train; B reads the sacks in the code,
# sees the heap and the parked train keep them. A takes the combine with 50 sacks of barley; B reads
# them from its flags. No terrain data needed. Output in test_output/tractornet_*.log.
#   GODOT=<exe> [PORT=] tools/tractornetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" tractornet
PORT=${PORT:-7881}
WORLD="--world fixture --chunks fixture:flat --traffic 0"
tc_server 400 120 "$OUT/tractornet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test
client() { tc_client 340 "$OUT/tractornet_$1.log" --connect 127.0.0.1:$PORT --name "Farmer$1" $WORLD --tractornet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[tractornet" "$OUT/tractornet_A.log" "$OUT/tractornet_B.log"
if tc_ok 2 "$OUT/tractornet_A.log" "$OUT/tractornet_B.log"; then echo "[tractornetcheck] RESULT: ok"; exit 0; fi
echo "[tractornetcheck] RESULT: FAILED (see $OUT/tractornet_*.log)"; exit 1
