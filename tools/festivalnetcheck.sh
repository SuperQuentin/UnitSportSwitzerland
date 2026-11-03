#!/usr/bin/env bash
# The festival framework over loopback (#1269, src/Occasions/Festivals + FestivalNetProbe): a dedicated server on the fixture,
# its calendar read as 11 March 2027 (day 2 of the check-only festival, --festival test). Everything is checked on the REMOTE
# peer. Lea and Tom form a party and play the bell: both members' points land on one board each sees, the first
# milestone pays only Lea (who had contributed alone), the second pays both, each sees its own pay in its section. Tom leaves
# holding his Batzen; Lea moves the server's calendar past the end and, online, is paid hers as francs at once. Tom comes back
# (same user://) and is paid as his progress loads; Lea comes back and is paid nothing twice (the script counts the server's
# bank lines). No terrain data needed.
#   GODOT=<exe> [PORT=] tools/festivalnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" festivalnet
PORT=${PORT:-7951}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-03-11 --systems network,occasions,adventure --admin-password festivalpw)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B B2 A2 server; do : > "$OUT/festivalnet_$f.log"; done
B_UD="$OUT/userdata_festivalnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 400 120 "$OUT/festivalnet_server.log" "${SARGS[@]}"
tc_client 300 "$OUT/festivalnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --festivalnet a &
A=$!
( tc_userdata "$B_UD"; tc_client 300 "$OUT/festivalnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --festivalnet b ) &
B=$!
wait $A $B
echo "[festivalnetcheck] Tom comes back after the end"
( tc_userdata "$B_UD"; tc_client 200 "$OUT/festivalnet_B2.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --festivalnet b2 )
echo "[festivalnetcheck] Lea comes back"
tc_client 200 "$OUT/festivalnet_A2.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --festivalnet a2
tc_stop
S="$OUT/festivalnet_server.log"
grep -h "\[festival\]" "$S"
grep -h "\[festivalnet" "$OUT"/festivalnet_A.log "$OUT"/festivalnet_B.log "$OUT"/festivalnet_B2.log "$OUT"/festivalnet_A2.log
paid=$(grep -c "credited 140 CHF (festival)" "$S")
lea=$(grep -c "Lea credited 140 CHF (festival)" "$S")
tom=$(grep -c "Tom credited 140 CHF (festival)" "$S")
echo "[festivalnetcheck] bank lines: $paid in all, Lea $lea, Tom $tom"
if tc_ok 4 "$OUT/festivalnet_A.log" "$OUT/festivalnet_B.log" "$OUT/festivalnet_B2.log" "$OUT/festivalnet_A2.log" \
   && [ "$lea" = 1 ] && [ "$tom" = 1 ] && [ "$paid" = 2 ] \
   && grep -q 'reached "Warm-up" (150 points): 1 member(s) paid 20 Batzen' "$S" \
   && grep -q 'reached "Crowd" (400 points): 2 member(s) paid 40 Batzen' "$S"; then
  echo "[festivalnetcheck] RESULT: ok"; exit 0
fi
echo "[festivalnetcheck] RESULT: FAILED (see $OUT/festivalnet_*.log)"; exit 1
