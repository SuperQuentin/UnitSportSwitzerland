#!/usr/bin/env bash
# A forklift moves a pallet, over loopback (#583 phase 2, src/Items/PalletNetProbe). A headless
# dedicated server on the GENERATED world, which puts a works at the edge of every village (#531), so
# this needs no terrain download. Both clients pick the nearest industrial bay whose hall has a loose
# floor pallet a forklift can drive square at. A (admin: a forklift out of nothing is an admin's)
# drives in through the bay, forks the pallet, drives back out with it and sets it down in the yard.
# B stands beside the bay and passes only on what reached it over the wire (the root CLAUDE.md's
# remote-peer rule): A's forks carrying a load inside and then outside, the hall's pallet taken and a
# loose one set down out in the yard (the server's PalletService), A's forks empty after.
#   GODOT=<exe> [PORT=] tools/palletnetcheck.sh      output in test_output/palletnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" palletnet
PORT=${PORT:-7879}
PW=pw583
WORLD="--generated-world --traffic 0"
tc_server 420 120 "$OUT/palletnet_server.log" --server --port $PORT --generated-world --admin-password $PW
# B first, so it is beside the bay when A arrives at it
tc_client 300 "$OUT/palletnet_B.log" --connect 127.0.0.1:$PORT --name PalletB $WORLD --palletnetcheck watch & B=$!
tc_client 300 "$OUT/palletnet_A.log" --connect 127.0.0.1:$PORT --name PalletA $WORLD --palletnetcheck drive $PW & A=$!
wait $A $B
tc_stop
grep -ah "\[palletnet\]" "$OUT/palletnet_A.log" "$OUT/palletnet_B.log"
grep -ah "\[pallets\]" "$OUT/palletnet_server.log" | tail -4
if tc_ok 2 "$OUT/palletnet_A.log" "$OUT/palletnet_B.log"; then echo "[palletnetcheck] RESULT: ok"; exit 0; fi
echo "[palletnetcheck] RESULT: FAILED (see $OUT/palletnet_*.log)"; exit 1
