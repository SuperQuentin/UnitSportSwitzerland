#!/usr/bin/env bash
# A building site's pallet of materials forked over loopback (#615, src/Items/SitePalletNetProbe):
# a headless dedicated server on the site fixture (fixture:site, #1718),
# with an admin password, and two headless clients at the site. A (admin) takes a
# telehandler and lifts the nearest site pallet, then sets it down; B passes only on what reached
# it: the pallet taken (the server worked it out from the tile's own files), on A's telehandler in
# A's pose and drawn on its boom, then a loose one set down and A's forks empty.
#   GODOT=<exe> [PORT=] tools/sitepalletnetcheck.sh      output in test_output/sitepalletnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" sitepalletnet
PORT=${PORT:-7887}
# A takes the nearest site pallet with a clear side to come at: on the fixture, the topped-out site past the pits
# (a pallet by a pit or a slab is passed over, as the generated world's foundations site at 2588796,1118441 once was)
# fixture:site (#1718): its sites stand within 400 m east of the start. Never --at on a fixture: a client lays the
# course at its --at, the server at the default spawn
WORLD="--world fixture --chunks fixture:site --traffic 0"
tc_server 420 120 "$OUT/sitepalletnet_server.log" --server --port $PORT --world fixture --chunks fixture:site --admin-password test
tc_client 300 "$OUT/sitepalletnet_A.log" --connect 127.0.0.1:$PORT --name SiteA $WORLD --sitepalletnet A & A=$!
tc_client 300 "$OUT/sitepalletnet_B.log" --connect 127.0.0.1:$PORT --name SiteB $WORLD --sitepalletnet B & B=$!
wait $A $B
tc_stop
grep -ah "\[sitepalletnet" "$OUT/sitepalletnet_A.log" "$OUT/sitepalletnet_B.log" | grep -v " heard "
grep -ah "\[pallets\]" "$OUT/sitepalletnet_server.log" | tail -4
if tc_ok 2 "$OUT/sitepalletnet_A.log" "$OUT/sitepalletnet_B.log"; then echo "[sitepalletnetcheck] RESULT: ok"; exit 0; fi
echo "[sitepalletnetcheck] RESULT: FAILED (see $OUT/sitepalletnet_*.log)"; exit 1
