#!/usr/bin/env bash
# Two saucers in orbit over loopback (src/Player/OrbitNetProbe, #1430 H5): a dedicated server on the flat fixture
# with an admin password and two headless clients, both admins. Side by side on the ground, both ask /orbit 400
# (the server checks, then sends it to each pilot's client), fly their orbits in doubles for a minute watching
# each other's copy (beside, no jump), then A thrusts down out of orbit into the hover while B sees it fall away.
# Output in test_output/orbitnet_*.log.
# No terrain data needed.
#   GODOT=<exe> [PORT=] tools/orbitnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" orbitnet
PORT=${PORT:-7987}
WORLD="--world fixture --chunks fixture:flat"
tc_server 420 120 "$OUT/orbitnet_server.log" --title "#1480 orbitnet server" --server --port $PORT $WORLD --admin-password test
client() { tc_client 420 "$OUT/orbitnet_$1.log" --title "#1480 orbitnet client $1" --connect 127.0.0.1:$PORT --name "Orbit$1" \
    $WORLD --orbitnetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[orbitnet\|\[ufo\]" "$OUT/orbitnet_A.log" "$OUT/orbitnet_B.log"
if tc_ok 2 "$OUT/orbitnet_A.log" "$OUT/orbitnet_B.log"; then echo "[orbitnetcheck] RESULT: ok"; exit 0; fi
echo "[orbitnetcheck] RESULT: FAILED (see $OUT/orbitnet_*.log)"; exit 1
