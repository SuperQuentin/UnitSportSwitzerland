#!/usr/bin/env bash
# Items dropped, thrown and picked up over loopback (#206): a dedicated server, a windowed thrower
# that drops a stone, winds up a full throw of another (screenshot of the arc at full charge:
# test_output/dropcheck_aim.png) and drops a stack of energy bars, and a windowed watcher that must see
# the wind-up pose and the three items settled (the thrown one far out), then point at the bars
# (screenshot of the outline: test_output/dropcheck_point.png) and pick them up.
#   tools/dropcheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/dropcheck.sh   (a worktree without terrain data)
. "$(dirname "$0")/lib/twoclient.sh" drop
PORT=7798
# a full-world server blends its horizon for half a minute before it listens: wait for it
tc_server 300 150 $OUT/dropcheck_server.log --server --port $PORT ${CH[@]+"${CH[@]}"}
sleep 2
tc_client 200 $OUT/dropcheck_thrower.log --windowed --connect 127.0.0.1:$PORT --name Thrower --dropcheck thrower --traffic 0 --cache "$PWD/$OUT/dropcheck_cache_thrower" ${CH[@]+"${CH[@]}"} &
THROWER=$!
sleep 2
tc_client 195 $OUT/dropcheck_watch.log --windowed --connect 127.0.0.1:$PORT --name Watcher --dropcheck watch --traffic 0 --cache "$PWD/$OUT/dropcheck_cache_watch" ${CH[@]+"${CH[@]}"}
code=$?
wait $THROWER   # runs may exit 139 after their result: read the RESULT line
grep -q "RESULT: ok" $OUT/dropcheck_thrower.log || code=1
grep -q "RESULT: ok" $OUT/dropcheck_watch.log || code=1
grep -h "\[dropcheck\]" $OUT/dropcheck_thrower.log $OUT/dropcheck_watch.log
tc_stop
exit $code
