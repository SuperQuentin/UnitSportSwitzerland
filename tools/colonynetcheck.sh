#!/usr/bin/env bash
# The Moon colonies on a server (src/World/Moon/ColonyNetProbe, #2000): a dedicated server hosting the Moon under
# --frame earth on the straight fixture builds Artemis Base round its players (ServerColonies) and runs its crew as bots on
# its own collision (the colony's interiors, #2005) through airlocks it owns (ColonyDoors), one of them sent on an EVA
# (--colonisteva). Two headless clients, both admins, go to the base: A watches the crew stand on the floors, walk the rooms
# and never pass a wall; B watches the EVA colonist go out through an airlock whose outer door it sees open.
# Output in test_output/colonynet_*.log. Needs the Moon's pyramid: BODIES=<dir> (default E:/UnitSportSwitzerland/planets).
#   GODOT=<exe> [PORT=] [BODIES=] tools/colonynetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" colonynet
PORT=${PORT:-7994}
BODIES=${BODIES:-E:/UnitSportSwitzerland/planets}
if [ ! -d "$BODIES/moon/cube" ]; then echo "[colonynetcheck] RESULT: FAILED (no Moon data in $BODIES/moon/cube: BODIES=<dir>)"; exit 1; fi
WORLD="--frame earth --world fixture --chunks fixture:straight --bodies $BODIES --sun real --date 2027-07-15 --time 10 --occasion none"
tc_server 700 180 "$OUT/colonynet_server.log" --title "#2000 colonynet server" --server --port $PORT $WORLD --admin-password test --colonisteva
client() { tc_client 700 "$OUT/colonynet_$1.log" --title "#2000 colonynet client $1" --connect 127.0.0.1:$PORT --name "Colony$1" \
    $WORLD --colonynetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[colonynet" "$OUT/colonynet_A.log" "$OUT/colonynet_B.log"
grep -h "\[colonies\]\|\[colonists\]" "$OUT/colonynet_server.log" | head -20
fail=0
grep -q "the server's Artemis Base: built" "$OUT/colonynet_server.log" || { echo "[colonynetcheck] the server never built the base"; fail=1; }
grep -q "artemis: the crew are bots now" "$OUT/colonynet_server.log" || { echo "[colonynetcheck] the server never ran the crew"; fail=1; }
if [ $fail = 0 ] && tc_ok 2 "$OUT/colonynet_A.log" "$OUT/colonynet_B.log"; then echo "[colonynetcheck] RESULT: ok"; exit 0; fi
echo "[colonynetcheck] RESULT: FAILED (see $OUT/colonynet_*.log)"; exit 1
