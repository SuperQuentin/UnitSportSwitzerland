#!/usr/bin/env bash
# Steinstossen over loopback (#1269 part 3, src/Occasions/Festivals/SteinNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 14 March 2027 (day 5 of the check-only festival, --festival test, when its stone opens). Everything is checked on the REMOTE peers: Lea walks
# to the stone, E opens an attempt on the server, a perfect put through the real input actions lands; her section holds the Batzen the server
# paid and a stake shows. Tom, 5 m away, is told the same landing (to the millimetre) and paid nothing. Lea's forged put (stamps from the
# future) is refused over the wire and uses up no put. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/steinnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" steinnet
PORT=${PORT:-7971}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-03-14 --systems network,occasions,adventure --admin-password steinpw)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/steinnet_$f.log"; done
B_UD="$OUT/userdata_steinnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 300 120 "$OUT/steinnet_server.log" "${SARGS[@]}"
tc_client 240 "$OUT/steinnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --steinnet a &
A=$!
( tc_userdata "$B_UD"; tc_client 240 "$OUT/steinnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --steinnet b ) &
B=$!
wait $A $B
tc_stop
grep -h "\[stein\]\|\[festival\]" "$OUT/steinnet_server.log"
grep -h "\[steinnet" "$OUT/steinnet_A.log" "$OUT/steinnet_B.log"
if tc_ok 2 "$OUT/steinnet_A.log" "$OUT/steinnet_B.log"; then
  echo "[steinnetcheck] RESULT: ok"; exit 0
fi
echo "[steinnetcheck] RESULT: FAILED (see $OUT/steinnet_*.log)"; exit 1
