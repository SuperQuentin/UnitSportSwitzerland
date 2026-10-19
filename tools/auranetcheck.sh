#!/usr/bin/env bash
# Aura battles over loopback (#907): a headless dedicated server on the flat fixture and two clients.
# A challenges B with /aura, B accepts; A plans the opening and cuts it on the beat (B must see A's
# copy strike the poses, both the same verdict); B answers it (the answer bonus), then leaves its last
# move uncut (frozen, a Miss); the server must refuse A's forged presses and a nine-move plan; A
# forfeits, and both must see B win and the battle end.
#   tools/auranetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/auranetcheck.sh    (B windowed, for a look at the camera and the HUD)
. "$(dirname "$0")/lib/twoclient.sh" auranet
PORT=${AURA_PORT:-7897}
WORLD="--world fixture"
tc_server 300 120 $OUT/auranet_server.log --server --port $PORT $WORLD
tc_client 260 $OUT/auranet_A.log --connect 127.0.0.1:$PORT --name AuraA $WORLD --auranet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 260 $OUT/auranet_B.log $WIN --connect 127.0.0.1:$PORT --name AuraB $WORLD --nocapture --auranet B
wait $A
tc_stop
grep -h "\[auranet\|\[aura\]" $OUT/auranet_A.log $OUT/auranet_B.log
grep -h "\[aura\]" $OUT/auranet_server.log
code=0
tc_ok 2 $OUT/auranet_A.log $OUT/auranet_B.log || code=1
# the forgeries: a press for the rival's turn, one stamped in the future, a nine-move plan
for what in "refused [0-9]*'s press 0 for turn 1" "stamped .* s ahead" "refused [0-9]*'s plan of 9"; do
  if ! grep -q "$what" $OUT/auranet_server.log; then echo "[auranetcheck] FAIL the server did not refuse: $what"; code=1; fi
done
echo "[auranetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
