#!/usr/bin/env bash
# The Escalade over loopback (#1351, src/Occasions/Festivals/Escalade/EscaladeNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 11 December 2027 (day 1 of 2, held at the fixture start under --festival test). Everything is checked on the REMOTE peers: Lea and Tom form a party;
# Tom is refused the Marmite and the pot from 150 m; Lea takes the Marmite (both are told she holds it), Tom takes the other side, both are paid in their
# own sections with the chocolate and hear the saying; Lea ladles at the pot and Tom, who pressed nothing, is fed; Tom runs the Course through the six gates
# (the server follows his position) and is paid; Lea sees the party's points on the shared board. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/escaladenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" escaladenet
PORT=${PORT:-7975}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-12-11 --escaladefast --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/escaladenet_$f.log"; done
B_UD="$OUT/userdata_escaladenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/escaladenet_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/escaladenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --escaladenet a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/escaladenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --escaladenet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[escaladenet] the server was still up when the clients ended" || echo "[escaladenet] the server had stopped before the clients ended"
tc_stop
S="$OUT/escaladenet_server.log"
grep -h "the Marmite smashed\|a bowl of soupe\|the Course in" "$S"
grep -h "\[escaladenet" "$OUT"/escaladenet_A.log "$OUT"/escaladenet_B.log
smashed=$(grep -c "the Marmite smashed" "$S")
bowls=$(grep -c "a bowl of soupe" "$S")
courses=$(grep -c "the Course in" "$S")
if tc_ok 2 "$OUT/escaladenet_A.log" "$OUT/escaladenet_B.log" && [ "$smashed" = 2 ] && [ "$bowls" = 2 ] && [ "$courses" = 1 ]; then
  echo "[escaladenetcheck] RESULT: ok"; exit 0
fi
echo "[escaladenetcheck] RESULT: FAILED (see $OUT/escaladenet_*.log; $smashed smash payments, $bowls bowls and $courses runs on the server, want 2, 2 and 1)"; exit 1
