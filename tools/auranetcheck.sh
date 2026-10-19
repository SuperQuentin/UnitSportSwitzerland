#!/usr/bin/env bash
# Aura battles over loopback (#907, #914): a headless dedicated server on the flat fixture and three
# clients. A challenges B with /aura, B accepts as E at A would; A plans the opening and cuts it on the
# beat (B must see A's copy strike the poses, both the same verdict) while C, a spectator, sees it on
# its screen and cheers (the crowd bonus); B breaks character on purpose (laughed first: A's bonus); B
# answers (the answer bonus) while A keeps a straight face, then leaves its last cut (frozen); the
# server must refuse A's forged presses and a nine-move plan; A forfeits and both see B win. C then
# adds a bot, challenges it, and the bot plans and cuts its own combo.
#   tools/auranetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/auranetcheck.sh    (B and C windowed, for a look at the camera and the HUDs)
. "$(dirname "$0")/lib/twoclient.sh" auranet
PORT=${AURA_PORT:-7897}
WORLD="--world fixture"
tc_server 420 120 $OUT/auranet_server.log --server --port $PORT $WORLD --admin-password test
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 380 $OUT/auranet_A.log --connect 127.0.0.1:$PORT --name AuraA $WORLD --auranet A &
A=$!
tc_client 380 $OUT/auranet_C.log $WIN --connect 127.0.0.1:$PORT --name AuraC $WORLD --nocapture --auranet C &
C=$!
tc_client 380 $OUT/auranet_B.log $WIN --connect 127.0.0.1:$PORT --name AuraB $WORLD --nocapture --auranet B
wait $A
wait $C
tc_stop
grep -h "\[auranet\|\[aura\]" $OUT/auranet_A.log $OUT/auranet_B.log $OUT/auranet_C.log
grep -h "\[aura\]" $OUT/auranet_server.log
code=0
tc_ok 3 $OUT/auranet_A.log $OUT/auranet_B.log $OUT/auranet_C.log || code=1
# the forgeries refused, a laugh, a bot battle: the server's own word
for what in "refused [0-9]*'s press 0 for turn 1" "stamped .* s ahead" "refused [0-9]*'s plan of 9" "laughed first" "bot .* battles"; do
  if ! grep -q "$what" $OUT/auranet_server.log; then echo "[auranetcheck] FAIL the server log lacks: $what"; code=1; fi
done
echo "[auranetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
