#!/usr/bin/env bash
# House taps and instruments over loopback (#433, docs/notes/terrain/house-props.md): a headless
# dedicated server on the generated world and two clients. A turns a sink's tap on in the nearest
# house, B walks in and must see the water run; A sits at an instrument and B must see it seated;
# A plays eight notes and B must hear most of them; A stands up, turns the tap off, B sees it off.
#   tools/housepropscheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/housepropscheck.sh  (both windowed: test_output/houseprops_*.png)
. "$(dirname "$0")/lib/twoclient.sh" houseprops
PORT=${HOUSEPROPS_PORT:-7853}
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_server 480 120 $OUT/houseprops_server.log --server --port $PORT --generated-world
tc_client 460 $OUT/houseprops_A.log $WIN --connect 127.0.0.1:$PORT --name TapA --traffic 0 --housepropsnet A &
A=$!
sleep 2
tc_client 455 $OUT/houseprops_B.log $WIN --connect 127.0.0.1:$PORT --name TapB --traffic 0 --housepropsnet B
wait $A
tc_stop
grep -ah "\[housepropsnet" $OUT/houseprops_A.log $OUT/houseprops_B.log
code=0
tc_ok 2 $OUT/houseprops_A.log $OUT/houseprops_B.log || code=1
echo "[housepropscheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
