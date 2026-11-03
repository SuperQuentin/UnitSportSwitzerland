#!/usr/bin/env bash
# The alphorn and yodel game over loopback (#1269 part 4, src/Occasions/Festivals/Horn/HornNetProbe.cs): a dedicated server on the fixture, its
# calendar read as 11 March 2027 (day 2 of the check-only festival, --festival test). Everything is checked on the REMOTE peers: Lea and Tom walk to
# the alphorn row and sit down to the same song (melody and second voice), a song sent too early is refused, both play it with the real game
# screen, the server grades both and scores their harmony, and each peer is told its own result and holds its own Batzen. No terrain data needed.
# Tom sits down within 45 s of Lea, so he joins her song.
#   GODOT=<exe> [PORT=] tools/hornnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" hornnet
PORT=${PORT:-7971}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-03-11 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/hornnet_$f.log"; done
tc_server 400 120 "$OUT/hornnet_server.log" "${SARGS[@]}"
tc_client 300 "$OUT/hornnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --hornnet a &
A=$!
tc_client 300 "$OUT/hornnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --hornnet b &
B=$!
wait $A $B
tc_stop
S="$OUT/hornnet_server.log"
grep -h "\[festival\]" "$S" | grep -i "alphorn"
grep -h "\[hornnet" "$OUT"/hornnet_A.log "$OUT"/hornnet_B.log
if tc_ok 2 "$OUT/hornnet_A.log" "$OUT/hornnet_B.log" \
   && grep -q "sits down as Melody" "$S" && grep -q "sits down as Third" "$S"; then
  echo "[hornnetcheck] RESULT: ok"; exit 0
fi
echo "[hornnetcheck] RESULT: FAILED (see $OUT/hornnet_*.log)"; exit 1
