#!/usr/bin/env bash
# Online between bodies (src/World/Bodies/MoonNetProbe, #1852 C3b of docs/plans/planets.md section 13): a dedicated server
# under --frame earth on the straight fixture, with an admin password, and two headless clients, both admins. B sets a
# telescope up and looks through it; A takes the fly camera to the Moon (/goto, the server's check) and steps in on the
# generated ground; B reads A's far marker (where A says it stands, on the Moon's disc, labelled in the eyepiece), then
# comes to the Moon beside A; each sees the other walk and jump; A adds two bots there, which the server runs in its Moon
# region on its own collision, and both see them stand. The server's log must show a Moon region and its ground.
# Output in test_output/moonnet_*.log. Needs the Moon's pyramid: BODIES=<dir> (default E:/UnitSportSwitzerland/planets).
#   GODOT=<exe> [PORT=] [BODIES=] tools/moonnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" moonnet
PORT=${PORT:-7993}
BODIES=${BODIES:-E:/UnitSportSwitzerland/planets}
if [ ! -d "$BODIES/moon/cube" ]; then echo "[moonnetcheck] RESULT: FAILED (no Moon data in $BODIES/moon/cube: BODIES=<dir>)"; exit 1; fi
WORLD="--frame earth --world fixture --chunks fixture:straight --bodies $BODIES --sun real --date 2027-07-15 --time 10 --occasion none"
tc_server 600 180 "$OUT/moonnet_server.log" --title "#1852 moonnet server" --server --port $PORT $WORLD --admin-password test
client() { tc_client 600 "$OUT/moonnet_$1.log" --title "#1852 moonnet client $1" --connect 127.0.0.1:$PORT --name "Moon$1" \
    $WORLD --moonnetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[moonnet" "$OUT/moonnet_A.log" "$OUT/moonnet_B.log"
grep -h "\[regions\].*Moon\|server's Moon" "$OUT/moonnet_server.log" | head -8
fail=0
grep -q "region .*(Moon ENU" "$OUT/moonnet_server.log" || { echo "[moonnetcheck] the server never made a Moon region"; fail=1; }
grep -q "the server's Moon ground: square" "$OUT/moonnet_server.log" || { echo "[moonnetcheck] the server never built Moon ground"; fail=1; }
if [ $fail = 0 ] && tc_ok 2 "$OUT/moonnet_A.log" "$OUT/moonnet_B.log"; then echo "[moonnetcheck] RESULT: ok"; exit 0; fi
echo "[moonnetcheck] RESULT: FAILED (see $OUT/moonnet_*.log)"; exit 1
