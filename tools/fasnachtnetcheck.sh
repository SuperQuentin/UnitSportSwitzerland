#!/usr/bin/env bash
# Basel's Fasnacht over loopback (#1357, src/Occasions/Festivals/Fasnacht/FasnachtNetProbe.cs): a dedicated server on the fixture, its calendar read as 15 February
# 2027 (day 1 of 3, held at the fixture start under --festival test); Lea, as admin, stops its clock at 04:30 so the Morgestraich is on. Everything is checked on the REMOTE
# peers: both see the street lamps out; Tom is refused the stall, the lantern and a seat from 150 m; both take a Larve and Räppli (each in its own section), put the Larve on
# and see the other's mask on the replicated body; both light the lantern; Lea throws and the paper flies on Tom's machine (relayed); Lea sits first (the drum), Tom second
# (the trumpet), each taps the whole part, the server grades both, scores the din and pays each. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/fasnachtnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" fasnachtnet
PORT=${PORT:-7976}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-02-15 --admin-password test --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure,sky"
for f in A B server; do : > "$OUT/fasnachtnet_$f.log"; done
B_UD="$OUT/userdata_fasnachtnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/fasnachtnet_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/fasnachtnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --fasnachtnet a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/fasnachtnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --fasnachtnet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[fasnachtnet] the server was still up when the clients ended" || echo "[fasnachtnet] the server had stopped before the clients ended"
tc_stop
S="$OUT/fasnachtnet_server.log"
grep -h "a Larve from the stall\|the Morgestraich\|played with the Guggenmusik" "$S"
grep -h "\[fasnachtnet" "$OUT"/fasnachtnet_A.log "$OUT"/fasnachtnet_B.log
larven=$(grep -c "a Larve from the stall" "$S")
marches=$(grep -c "the Morgestraich" "$S")
songs=$(grep -c "played with the Guggenmusik" "$S")
if tc_ok 2 "$OUT/fasnachtnet_A.log" "$OUT/fasnachtnet_B.log" && [ "$larven" = 2 ] && [ "$marches" = 2 ] && [ "$songs" = 2 ]; then
  echo "[fasnachtnetcheck] RESULT: ok"; exit 0
fi
echo "[fasnachtnetcheck] RESULT: FAILED (see $OUT/fasnachtnet_*.log; $larven stall payments, $marches Morgestraich payments and $songs song payments on the server, want 2, 2 and 2)"; exit 1
