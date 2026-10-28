#!/usr/bin/env bash
# The world events over loopback (#1339, src/Adventure/Errands/WorldEvents + WorldEventNetProbe): a dedicated server on the flat
# fixture planning an avalanche and then a forest fire once two players are there (checkerboard forest, no raster on a flat world),
# Lea (lead) and Tom (follow, its own user://). Each sees the plan the server sent, digs out the buried (a dig from 30 m off is
# refused), beats out the burning cells the server sent, and is paid: the count and the reputation reach each one's own section.
#   GODOT=<exe> [PORT=] tools/worldeventnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" worldeventnet
PORT=${PORT:-7939}
WORLD="--world fixture --chunks fixture:flat --pois fixture --givers fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure,sky --worldevent avalanche,fire --worldevent-forest --worldevent-players 2)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/worldeventnet_A.log"; : > "$OUT/worldeventnet_B.log"
tc_server 600 120 "$OUT/worldeventnet_server.log" "${SARGS[@]}"
tc_client 540 "$OUT/worldeventnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --worldeventnet lead &
A=$!
B_UD="$OUT/userdata_worldeventnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 540 "$OUT/worldeventnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --worldeventnet follow ) &
B=$!
wait $A $B
tc_stop
grep -h "\[worldevents\]\|\[worldeventnet" "$OUT/worldeventnet_server.log" "$OUT/worldeventnet_A.log" "$OUT/worldeventnet_B.log" | grep -v " ok " | head -80
if tc_ok 2 "$OUT/worldeventnet_A.log" "$OUT/worldeventnet_B.log" && grep -q "Avalanche .* ends: helped through" "$OUT/worldeventnet_server.log" && grep -q "ForestFire .* ends: helped through" "$OUT/worldeventnet_server.log"; then
  echo "[worldeventnetcheck] RESULT: ok"; exit 0
fi
echo "[worldeventnetcheck] RESULT: FAILED (see $OUT/worldeventnet_*.log)"; exit 1
