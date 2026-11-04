#!/usr/bin/env bash
# An admin's /movespeed over loopback (#1485, src/Player/MoveSpeedNetProbe): a headless dedicated server on the
# flat fixture with an admin password and two clients. Both are refused as plain players; A logs in, walks at x8
# and at x1, and B must see A's copy cover the ground at each speed; then A sets a bot's factor by its name.
#   tools/movespeednetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" movespeednet
PORT=${MOVESPEED_PORT:-7905}
WORLD="--world fixture --chunks fixture:flat"
tc_server 240 120 $OUT/movespeednet_server.log --server --port $PORT $WORLD --admin-password movespeedcheck
tc_client 200 $OUT/movespeednet_A.log --connect 127.0.0.1:$PORT --name WalkerA $WORLD --traffic 0 --movespeednet A &
A=$!
tc_client 200 $OUT/movespeednet_B.log --connect 127.0.0.1:$PORT --name WatcherB $WORLD --traffic 0 --movespeednet B
wait $A
tc_stop
grep -h "\[movespeednet" $OUT/movespeednet_A.log $OUT/movespeednet_B.log
grep -h "\[admin\]" $OUT/movespeednet_server.log
code=0
tc_ok 2 $OUT/movespeednet_A.log $OUT/movespeednet_B.log || code=1
echo "[movespeednetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
