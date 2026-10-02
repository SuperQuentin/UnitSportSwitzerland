#!/usr/bin/env bash
# Thrown things hitting a player, and a radio carried on the back, over loopback (#261): a headless
# dedicated server on a generated world, a headless thrower that throws a stone then a radio at the
# victim, a headless victim that must be hurt, never below the floor, never knocked out, and see the
# thrower's radio on its back. No terrain data needed.
#   tools/bonkcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" bonk
PORT=${BONK_PORT:-7833}
tc_server 900 120 $OUT/bonkcheck_server.log --server --port $PORT --generated-world
tc_client 200 $OUT/bonkcheck_thrower.log --connect 127.0.0.1:$PORT --name Thrower --bonkcheck thrower --traffic 0 &
THROWER=$!
sleep 2
tc_client 195 $OUT/bonkcheck_victim.log --connect 127.0.0.1:$PORT --name Victim --bonkcheck victim --traffic 0
wait $THROWER
code=0
grep -q "RESULT: ok" $OUT/bonkcheck_thrower.log || code=1
grep -q "RESULT: ok" $OUT/bonkcheck_victim.log || code=1
grep -h "\[bonkcheck\]" $OUT/bonkcheck_thrower.log $OUT/bonkcheck_victim.log
grep -h "\[bonk\]" $OUT/bonkcheck_server.log
echo "[bonkcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
