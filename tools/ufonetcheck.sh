#!/usr/bin/env bash
# The flying saucer over loopback (src/Player/UfoNetProbe, #861): a dedicated server on the flat fixture
# with an admin password and two headless clients (A logs in, so it may park the saucer). A takes a saucer (B sees it on its legs, ramp open), hovers over B (legs up,
# ramp shut), switches the beam on (B sees it and is lifted, A sees B rise), B walks out (let down,
# unhurt), beam and lamps off (B sees both), A lands (ramp open) and gets out (B sees it parked).
# Output in test_output/ufonet_*.log.
# No terrain data needed.
#   GODOT=<exe> [PORT=] tools/ufonetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" ufonet
PORT=${PORT:-7911}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/ufonet_server.log" --title "#861 ufonet server" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/ufonet_$1.log" --title "#861 ufonet client $1" --connect 127.0.0.1:$PORT --name "Ufo$1" \
    $WORLD --ufonetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[ufonet\|\[beam\]" "$OUT/ufonet_A.log" "$OUT/ufonet_B.log"
if tc_ok 2 "$OUT/ufonet_A.log" "$OUT/ufonet_B.log"; then echo "[ufonetcheck] RESULT: ok"; exit 0; fi
echo "[ufonetcheck] RESULT: FAILED (see $OUT/ufonet_*.log)"; exit 1
