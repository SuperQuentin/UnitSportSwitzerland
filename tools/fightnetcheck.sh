#!/usr/bin/env bash
# Fist fights over loopback (#495): a headless dedicated server on the flat fixture and two clients.
# A challenges B with /fight, B accepts as E at A would; A steps in and jabs until it wins round 1
# (B must see A's jab pose and lose HP), a 30-strike burst in round 2 must be held by the server's
# rate budget, then A forfeits and both must see the match end.
#   tools/fightnetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/fightnetcheck.sh    (B windowed, for a look at the fight camera and the HUD)
. "$(dirname "$0")/lib/twoclient.sh" fightnet
PORT=${FIGHT_PORT:-7853}
WORLD="--world fixture"
tc_server 260 120 $OUT/fightnet_server.log --server --port $PORT $WORLD
tc_client 220 $OUT/fightnet_A.log --connect 127.0.0.1:$PORT --name FighterA $WORLD --fightnet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 220 $OUT/fightnet_B.log $WIN --connect 127.0.0.1:$PORT --name FighterB $WORLD --nocapture --fightnet B
wait $A
tc_stop
grep -h "\[fightnet\|\[fight\]" $OUT/fightnet_A.log $OUT/fightnet_B.log
grep -h "\[fight\]" $OUT/fightnet_server.log
code=0
tc_ok 2 $OUT/fightnet_A.log $OUT/fightnet_B.log || code=1
echo "[fightnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
