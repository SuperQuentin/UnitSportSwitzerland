#!/usr/bin/env bash
# The e-bike over loopback (#1167): a headless dedicated server on the flat fixture and two clients that
# watch each other ride. A rides the Samedi under its motor and B must see A's copy as that e-bike, on its
# own frame, moving and pedalling; then B rides the low frame and A must see B's copy the same way.
#   tools/ebikenetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" ebikenet
PORT=${EBIKE_PORT:-7917}
WORLD="--world fixture"
tc_server 240 120 $OUT/ebikenet_server.log --server --port $PORT $WORLD
tc_client 220 $OUT/ebikenet_A.log --connect 127.0.0.1:$PORT --name RiderA $WORLD --ebikenet A &
A=$!
B_UD="$OUT/userdata_ebikenet_B"; mkdir -p "$B_UD"; find "$B_UD" -mindepth 1 -delete
( tc_userdata "$B_UD"; tc_client 220 $OUT/ebikenet_B.log --connect 127.0.0.1:$PORT --name RiderB $WORLD --view third --nocapture --ebikenet B )
wait $A
tc_stop
grep -h "\[ebikenet" $OUT/ebikenet_A.log $OUT/ebikenet_B.log
code=0
tc_ok 2 $OUT/ebikenet_A.log $OUT/ebikenet_B.log || code=1
echo "[ebikenetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
