#!/usr/bin/env bash
# Adventure navigation over loopback (#1226, src/Adventure/Navigation/AdventureTravel + NavNetProbe): a dedicated server
# with --adventure-travel network on the flat fixture with the fixture POIs. A (Lea) gets the server's Network mode and
# no free jumps (/city refused); walking to the bus stop unlocks it; A and B (Tom, its own user://) make a party and A's
# shared pin reaches B; a trip to a locked point and one from the map without the GA are refused; with CHF 500 from B (an
# admin) the trip from the station's sign is charged by the server and B sees A's remote body arrive at the bus stop;
# the GA bought at the station travels from anywhere for nothing; in a Battle Royale lobby a trip is refused; A leaves
# the party and its pin goes from B.
#   GODOT=<exe> [PORT=] tools/navnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" navnet
PORT=${PORT:-7931}
WORLD="--world fixture --chunks fixture:flat --pois fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure --adventure-travel network --admin-password navpw)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/navnet_A.log"; : > "$OUT/navnet_B.log"
tc_server 400 120 "$OUT/navnet_server.log" "${SARGS[@]}"
tc_client 360 "$OUT/navnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --navnet walk &
A=$!
B_UD="$OUT/userdata_navnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 360 "$OUT/navnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --navnet watch ) &
B=$!
wait $A $B
tc_stop
grep -h "\[travel\|\[pins\]\|\[nav\|\[navnet" "$OUT/navnet_server.log" "$OUT/navnet_A.log" "$OUT/navnet_B.log"
if tc_ok 2 "$OUT/navnet_A.log" "$OUT/navnet_B.log" && grep -q "travels to busstop:" "$OUT/navnet_server.log"; then
  echo "[navnetcheck] RESULT: ok"; exit 0
fi
echo "[navnetcheck] RESULT: FAILED (see $OUT/navnet_*.log)"; exit 1
