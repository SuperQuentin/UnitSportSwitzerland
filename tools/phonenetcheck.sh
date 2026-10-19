#!/usr/bin/env bash
# The phone over loopback (#889): a headless dedicated server on the fixture and two clients. A takes
# the phone out, opens it and puts it away; B must see A's copy hold it folded in both hands, open to
# the hinge's end, then put away with the arms back down.
#   tools/phonenetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/phonenetcheck.sh    (B windowed: its view of A in test_output/phonenet_B_*.png)
. "$(dirname "$0")/lib/twoclient.sh" phonenet
PORT=${PHONE_PORT:-7857}
WORLD="--world fixture"
tc_server 240 120 $OUT/phonenet_server.log --server --port $PORT $WORLD
tc_client 200 $OUT/phonenet_A.log --connect 127.0.0.1:$PORT --name CallerA $WORLD --phonenet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 200 $OUT/phonenet_B.log $WIN --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view third --nocapture --phonenet B
wait $A
tc_stop
grep -h "\[phonenet" $OUT/phonenet_A.log $OUT/phonenet_B.log
code=0
tc_ok 2 $OUT/phonenet_A.log $OUT/phonenet_B.log || code=1
echo "[phonenetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
