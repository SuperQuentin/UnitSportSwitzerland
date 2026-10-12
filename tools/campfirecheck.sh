#!/usr/bin/env bash
# A campfire over loopback (#272, src/Crafting/CampfireNetProbe): a headless dedicated server on the
# fixture world; headless client A crafts a campfire and lays it through the item's real path, then
# holds a torch; client B joins after, must get the fire from the join snapshot, burning (its light
# is there), see A's torch light, be refused putting out A's fire, then see it go when A puts it out.
#   tools/campfirecheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# The server's placed objects live in this run's own user:// (tools/lib/twoclient.sh), never the real one.
. "$(dirname "$0")/lib/twoclient.sh" campfire
PORT=${CAMPFIRE_PORT:-7843}

tc_server 1800 120 $OUT/campfire_server.log --server --port $PORT --world fixture
tc_client 300 $OUT/campfire_A.log --connect 127.0.0.1:$PORT --name FireA --campfirenet A --world fixture --traffic 0 &
A=$!
# B joins only once A's fire is lit: what it sees comes from the join snapshot
for _ in $(seq 1 200); do grep -q "say lit" $OUT/campfire_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
tc_client 240 $OUT/campfire_B.log --connect 127.0.0.1:$PORT --name FireB --campfirenet B --world fixture --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/campfire_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[campfirenet" $OUT/campfire_A.log $OUT/campfire_B.log
grep -h "\[placed\]" $OUT/campfire_server.log
echo "[campfirecheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
