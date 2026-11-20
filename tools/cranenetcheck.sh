#!/usr/bin/env bash
# A building site's crane worked over loopback (#618, src/Terrain/Construction/CraneNetProbe): a
# headless dedicated server on the site fixture (fixture:site, #1718),
# with an admin password, and two headless clients at a site. A takes the nearest crane's
# radio remote, slews it onto one of the site's pallets on the real levers, hooks it on, lifts it
# onto the top slab and sets it down, then lets go and speeds the simulation up. B passes only on
# what reached it: the crane held by A and drawn where A has it, the pallet taken and on the hook,
# then loose on the slab with its load, and the crane held where A left it, then back on its
# schedule.
#   GODOT=<exe> [PORT=] tools/cranenetcheck.sh      output in test_output/cranenet_*.log
. "$(dirname "$0")/lib/twoclient.sh" cranenet
PORT=${PORT:-7893}
# fixture:site (#1718): its sites stand within 400 m east of the start. Never --at on a fixture: a client lays the
# course at its --at, the server at the default spawn
WORLD="--world fixture --chunks fixture:site --traffic 0"
tc_server 480 120 "$OUT/cranenet_server.log" --server --port $PORT --world fixture --chunks fixture:site --admin-password test
tc_client 360 "$OUT/cranenet_A.log" --connect 127.0.0.1:$PORT --name CraneA $WORLD --cranenet A & A=$!
tc_client 360 "$OUT/cranenet_B.log" --connect 127.0.0.1:$PORT --name CraneB $WORLD --cranenet B & B=$!
wait $A $B
tc_stop
grep -ah "\[cranenet" "$OUT/cranenet_A.log" "$OUT/cranenet_B.log" | grep -v " heard "
grep -ah "\[cranes\]" "$OUT/cranenet_server.log" | tail -6
if tc_ok 2 "$OUT/cranenet_A.log" "$OUT/cranenet_B.log"; then echo "[cranenetcheck] RESULT: ok"; exit 0; fi
echo "[cranenetcheck] RESULT: FAILED (see $OUT/cranenet_*.log)"; exit 1
