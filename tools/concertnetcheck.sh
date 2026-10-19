#!/usr/bin/env bash
# A concert run live over loopback (#885): a headless dedicated server and two clients on the
# fixture world at Stade de Genève. A logs in, hosts set 7, turns its show input on and plays a
# controller (Art-Net to its own port, then an OSC blackout); B must get the set announced, A's
# universes relayed with their bytes, and the blackout cue; A releases it.
#   tools/concertnetcheck.sh   (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" concertnet
PORT=${CONCERT_PORT:-7895}
WORLD="--world fixture --at 2498751,1114890"
tc_server 300 120 $OUT/concertnet_server.log --server --port $PORT --admin-password test $WORLD
tc_client 240 $OUT/concertnet_A.log --connect 127.0.0.1:$PORT --name ShowA $WORLD --concertnet A &
A=$!
tc_client 240 $OUT/concertnet_B.log --connect 127.0.0.1:$PORT --name WatchB $WORLD --concertnet B
wait $A
tc_stop
grep -h "\[concertnet" $OUT/concertnet_A.log $OUT/concertnet_B.log
code=0
tc_ok 2 $OUT/concertnet_A.log $OUT/concertnet_B.log || code=1
echo "[concertnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
