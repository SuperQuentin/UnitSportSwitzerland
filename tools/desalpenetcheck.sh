#!/usr/bin/env bash
# The Desalpe over loopback (#1269 part 7, src/Occasions/Festivals/Desalpe/DesalpeNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 24 September 2027 (day 5 of the Desalpe, held at the fixture start under --festival test, a 110 m route). Everything is checked on the REMOTE peers:
# Lea dresses a cow at the alp and drives the herd down; Tom stands 150 m away on the far side of the fair. Tom sees the dress and the herd on the wire,
# is refused a dress and a start from where he stands, is not paid for Lea's drive, then votes in the ring for her herd and is paid; Lea sees the vote and is
# refused her own. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/desalpenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" desalpenet
PORT=${PORT:-7973}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-09-24 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/desalpenet_$f.log"; done
B_UD="$OUT/userdata_desalpenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/desalpenet_server.log" "${SARGS[@]}"
tc_client 520 "$OUT/desalpenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --desalpenet a &
A=$!
( tc_userdata "$B_UD"; tc_client 520 "$OUT/desalpenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --desalpenet b ) &
B=$!
wait $A $B
tc_stop
S="$OUT/desalpenet_server.log"
grep -h "\[desalpe\]\|dressed cow\|drove the herd\|voted for" "$S"
grep -h "\[desalpenet" "$OUT"/desalpenet_A.log "$OUT"/desalpenet_B.log
drove=$(grep -c "drove the herd" "$S")
if tc_ok 2 "$OUT/desalpenet_A.log" "$OUT/desalpenet_B.log" && [ "$drove" = 1 ]; then
  echo "[desalpenetcheck] RESULT: ok"; exit 0
fi
echo "[desalpenetcheck] RESULT: FAILED (see $OUT/desalpenet_*.log; $drove drive payments on the server)"; exit 1
