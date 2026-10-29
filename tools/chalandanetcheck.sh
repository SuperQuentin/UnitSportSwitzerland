#!/usr/bin/env bash
# The Chalandamarz over loopback (#1356, src/Occasions/Festivals/Chalanda/ChalandaNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 1 March 2027 (day 1 of 1, held at the fixture start under --festival test). Everything is checked on the REMOTE peers: Lea and Tom form a party;
# Tom is refused a start and a ring from 150 m; both begin the procession and ring the Treichel on 14 beats of the shared march clock, walking the loop:
# the server grades the stamps and tells both which beats were in step (each one's points are 3 a ring plus 2 a beat in step); both pass the seven stations
# (the server follows their positions) and finish at the stand, each paid in their own section with the Nusstorte and the Treichel; Lea sees the party's
# points on the shared board add up to both totals. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/chalandanetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" chalandanet
PORT=${PORT:-7976}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-03-01 --chalandafast --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/chalandanet_$f.log"; done
B_UD="$OUT/userdata_chalandanet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/chalandanet_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/chalandanet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --chalandanet a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/chalandanet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --chalandanet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[chalandanet] the server was still up when the clients ended" || echo "[chalandanet] the server had stopped before the clients ended"
tc_stop
S="$OUT/chalandanet_server.log"
grep -h "the procession in" "$S"
grep -h "\[chalandanet" "$OUT"/chalandanet_A.log "$OUT"/chalandanet_B.log
marches=$(grep -c "the procession in" "$S")
if tc_ok 2 "$OUT/chalandanet_A.log" "$OUT/chalandanet_B.log" && [ "$marches" = 2 ]; then
  echo "[chalandanetcheck] RESULT: ok"; exit 0
fi
echo "[chalandanetcheck] RESULT: FAILED (see $OUT/chalandanet_*.log; $marches procession payments on the server, want 2)"; exit 1
