#!/usr/bin/env bash
# The crash ragdoll over loopback (#214): a dedicated server, a headless driver (a) that takes a car
# and drives into a wall until it is thrown through the windscreen, and a windowed watcher (b) that
# must see a's copy go limp, follow a's replicated position and get up when a does.
#   tools/crashnetcheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/crashnetcheck.sh   (a worktree without terrain data)
. "$(dirname "$0")/lib/twoclient.sh" crashnet
PORT=7814
PW=crashcheck
# a full-world server blends its horizon for half a minute before it listens: wait for it
tc_server 260 150 $OUT/crashnet_server.log --server --port $PORT --admin-password $PW "${CH[@]}"
sleep 2
tc_client 200 $OUT/crashnet_b.log --windowed --connect 127.0.0.1:$PORT --name Watcher --crashnet b --traffic 0 --cache "$PWD/$OUT/crashnet_cache_b" "${CH[@]}" &
WATCHER=$!
sleep 3
tc_client 190 $OUT/crashnet_a.log --connect 127.0.0.1:$PORT --name Driver --crashnet a $PW --traffic 0 --cache "$PWD/$OUT/crashnet_cache_a" "${CH[@]}"
wait $WATCHER   # headless runs may exit 139 after their result: read the RESULT lines
code=0
grep -q "a RESULT: ok" $OUT/crashnet_a.log || code=1
grep -q "b RESULT: ok" $OUT/crashnet_b.log || code=1
grep -h "\[crashnet\]" $OUT/crashnet_a.log $OUT/crashnet_b.log
tc_stop
exit $code
