#!/usr/bin/env bash
# Emotes over loopback (#404): a headless dedicated server on the flat fixture and two clients, no
# music anywhere. A plays two emotes off the wheel's catalog and stops; B must see A's copy dance
# each one at full weight on the beat its own shared clock gives, then come back to rest.
#   tools/emotenetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/emotenetcheck.sh    (B windowed: its view of A in test_output/emotenet_B_*.png)
. "$(dirname "$0")/lib/twoclient.sh" emotenet
PORT=${EMOTE_PORT:-7851}
WORLD="--world fixture"
tc_server 240 120 $OUT/emotenet_server.log --server --port $PORT $WORLD
tc_client 200 $OUT/emotenet_A.log --connect 127.0.0.1:$PORT --name EmoterA $WORLD --emotenet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 200 $OUT/emotenet_B.log $WIN --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view third --nocapture --emotenet B
wait $A
tc_stop
grep -h "\[emotenet" $OUT/emotenet_A.log $OUT/emotenet_B.log
code=0
tc_ok 2 $OUT/emotenet_A.log $OUT/emotenet_B.log || code=1
echo "[emotenetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
