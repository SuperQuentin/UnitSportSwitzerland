#!/usr/bin/env bash
# Adventure places over loopback (#1224, src/Adventure/Places/PlacesService + PlacesNetProbe): a dedicated server
# on the flat fixture with the fixture POIs finds places for every player. A walks up the peak: the server stamps
# it and A's mirror and fog show it; driving to the tower finds nothing, on foot it does; a jump onto the station
# stamps nothing; E at the summit cross syncs it (its 3 km disc reaches A's fog). B, on at the same time with its
# own user://, gets none of it. The server restarts; A comes back with the same user://: stamps, view and fog kept.
#   GODOT=<exe> [PORT=] tools/placesnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" placesnet
PORT=${PORT:-7929}
WORLD="--world fixture --chunks fixture:flat --pois fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/placesnet_A1.log"; : > "$OUT/placesnet_A2.log"; : > "$OUT/placesnet_B.log"
tc_server 300 120 "$OUT/placesnet_server1.log" "${SARGS[@]}"
tc_client 240 "$OUT/placesnet_A1.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --placesnet walk &
A=$!
B_UD="$OUT/userdata_placesnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 240 "$OUT/placesnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --placesnet watch ) &
B=$!
wait $A $B
tc_stop
echo "[placesnetcheck] server stopped; restarting it on the same user://"
tc_server 300 120 "$OUT/placesnet_server2.log" "${SARGS[@]}"
tc_client 200 "$OUT/placesnet_A2.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --placesnet back
tc_stop
grep -h "\[places\|\[placesnet" "$OUT/placesnet_server1.log" "$OUT/placesnet_server2.log" "$OUT/placesnet_A1.log" \
  "$OUT/placesnet_B.log" "$OUT/placesnet_A2.log"
if tc_ok 3 "$OUT/placesnet_A1.log" "$OUT/placesnet_B.log" "$OUT/placesnet_A2.log" \
   && grep -q "synced peak:" "$OUT/placesnet_server1.log"; then
  echo "[placesnetcheck] RESULT: ok"; exit 0
fi
echo "[placesnetcheck] RESULT: FAILED (see $OUT/placesnet_*.log)"; exit 1
