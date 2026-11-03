#!/usr/bin/env bash
# Free climbing over loopback (#1225): a headless dedicated server on the crag fixture course, run with
# --stamina off --lift off, and two clients. Both must get those rules through Arrival.Rules (no meter,
# no lift, climbing still on: it is the Adventure layer's). A climbs the rock wall; B, 9 m off, must see
# A's copy in the wall-climbing pose where A says it is (0.3 m) and leant into the face; A tops out onto
# the ledge and B sees it stand there. No terrain data needed.
#   tools/climbnetcheck.sh             (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/climbnetcheck.sh     (B windowed: its view of A in test_output/climbnet_B_*.png)
. "$(dirname "$0")/lib/twoclient.sh" climbnet
PORT=${CLIMB_PORT:-7929}

WORLD="--world fixture --chunks fixture:crag"
tc_server 900 120 $OUT/climbnet_server.log --server --port $PORT $WORLD --systems network,adventure --stamina off --lift off
tc_client 300 $OUT/climbnet_A.log --connect 127.0.0.1:$PORT --name ClimberA $WORLD --systems network,physics,ui,adventure --climbnet A &
A=$!
WINDOW=; [ -n "${SHOTS:-}" ] && WINDOW=--windowed
tc_client 300 $OUT/climbnet_B.log $WINDOW --connect 127.0.0.1:$PORT --name WatcherB $WORLD --systems network,physics,ui,adventure --view third --nocapture --time 14 --climbnet B
for _ in $(seq 1 60); do kill -0 "$A" 2>/dev/null || break; guard_nap 0.5; done
tc_stop
grep -h "\[climbnet\|\[arrival\]" $OUT/climbnet_server.log $OUT/climbnet_A.log $OUT/climbnet_B.log
code=0
grep -q "\[climbnet A\] RESULT: ok" $OUT/climbnet_A.log || code=1
grep -q "\[climbnet B\] RESULT: ok" $OUT/climbnet_B.log || code=1
echo "[climbnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
