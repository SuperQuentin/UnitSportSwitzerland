#!/usr/bin/env bash
# Name tags over loopback (#1136): a headless dedicated server on the flat fixture and two clients.
# A plays as a phone (--mobile); B must see A's copy wear its name and MOBILE over its head, wear
# none itself, and see A's tag follow /name.
#   tools/nametagnetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" nametagnet
PORT=${NAMETAG_PORT:-7913}
WORLD="--world fixture"
tc_server 240 120 $OUT/nametagnet_server.log --server --port $PORT $WORLD
tc_client 200 $OUT/nametagnet_A.log --connect 127.0.0.1:$PORT --name TaggerA $WORLD --mobile --nametagnet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 200 $OUT/nametagnet_B.log $WIN --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view third --nocapture --nametagnet B
wait $A
tc_stop
grep -h "\[nametagnet" $OUT/nametagnet_A.log $OUT/nametagnet_B.log
code=0
tc_ok 2 $OUT/nametagnet_A.log $OUT/nametagnet_B.log || code=1
echo "[nametagnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
