#!/usr/bin/env bash
# One player at a time at a telescope over loopback (#1440, src/Items/TelescopeNetProbe): a headless
# dedicated server on the fixture world; client A sets a telescope up through the item's real path
# and looks through it; client B beside it is told someone is looking and is refused packing it up,
# then looks through it once A steps back; A packs it up.
#   tools/telescopecheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# The server's placed objects live in this run's own user:// (tools/lib/twoclient.sh), never the real one.
. "$(dirname "$0")/lib/twoclient.sh" telescope
PORT=${TELESCOPE_PORT:-7981}

tc_server 900 120 $OUT/telescope_server.log --server --port $PORT --world fixture
tc_client 300 $OUT/telescope_A.log --connect 127.0.0.1:$PORT --name ScopeA --telescopenet A --world fixture --traffic 0 &
A=$!
tc_client 300 $OUT/telescope_B.log --connect 127.0.0.1:$PORT --name ScopeB --telescopenet B --world fixture --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/telescope_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[telescopenet" $OUT/telescope_A.log $OUT/telescope_B.log
grep -h "\[telescope\]\|\[placed\]" $OUT/telescope_server.log
echo "[telescopecheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
