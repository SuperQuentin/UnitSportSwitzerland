#!/usr/bin/env bash
# Boats over loopback (#302): a headless dedicated server on the lake fixture course, gamey, with an
# admin password, and two clients. A (admin) idles a speedboat in the swell, runs it slowly, then
# leaves it floating; B swims 30 m off and must draw A's boat (driven, then parked) on B's own copy
# of the waves at B's own time: its pitch following B's surface as A's follows A's, its keel as deep
# under B's surface as A's is under A's. Then B swims into the parked boat's side and must meet its hull
# where B draws it (#378). No terrain data needed.
#   tools/boatnetcheck.sh             (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/boatnetcheck.sh     (B windowed: its view of A's boat, and of itself at its hull, in test_output/boatnet_B_*.png)
#   STYLE=ps1 SHOTS=1 ...             (that style for B)
. "$(dirname "$0")/lib/twoclient.sh" boatnet
PORT=${BOAT_PORT:-7845}

WORLD="--world fixture --chunks fixture:lake"
tc_server 1800 120 $OUT/boatnet_server.log --server --port $PORT $WORLD --sea-state gamey --admin-password test
tc_client 360 $OUT/boatnet_A.log --connect 127.0.0.1:$PORT --name BoaterA $WORLD --boatnet A &
A=$!
WINDOW=; [ -n "${SHOTS:-}" ] && WINDOW=--windowed
tc_client 300 $OUT/boatnet_B.log $WINDOW --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view first --nocapture --time 14 ${STYLE:+--style $STYLE} --boatnet B
for _ in $(seq 1 60); do kill -0 "$A" 2>/dev/null || break; guard_nap 0.5; done
grep -h "\[boatnet" $OUT/boatnet_A.log $OUT/boatnet_B.log
code=0
grep -q "\[boatnet A\] RESULT: ok" $OUT/boatnet_A.log || code=1
grep -q "\[boatnet B\] RESULT: ok" $OUT/boatnet_B.log || code=1
echo "[boatnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
