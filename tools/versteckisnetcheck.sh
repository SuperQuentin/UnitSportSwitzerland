#!/usr/bin/env bash
# Versteckis over loopback (#1268, src/Adventure/Versteckis + VersteckisNetProbe): a dedicated server on the fixture
# hamlet, no bots, two friends play two rounds. Checked on the REMOTE peer: B is withheld from the seeker A in Hide and
# A is held at the church door; B disguised as a milk can is drawn as one on A; a miss, a forged far tag refused and a
# catch all judged by the server; A as a hay bale on B, the bell breaking its disguise on B, its +3 on both; an Adventure
# request refused in the round; both get the versteckis section and are paid the same way (the server's base pay).
#   GODOT=<exe> [PORT=] tools/versteckisnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" versteckisnet
PORT=${PORT:-7941}
WORLD="--world fixture --chunks fixture:hamlet --pois fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure --adventure-test --admin-password vspw --vsmin 2)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/versteckisnet_A.log"; : > "$OUT/versteckisnet_B.log"
tc_server 400 120 "$OUT/versteckisnet_server.log" "${SARGS[@]}"
tc_client 380 "$OUT/versteckisnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --vsnet a --checkspeed 4 &
A=$!
B_UD="$OUT/userdata_versteckisnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 380 "$OUT/versteckisnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --vsnet b ) &
B=$!
wait $A $B
tc_stop
grep -h "\[vs\]\|\[vsnet" "$OUT/versteckisnet_server.log" "$OUT/versteckisnet_A.log" "$OUT/versteckisnet_B.log"
S="$OUT/versteckisnet_server.log"
lea=$(grep -o "paid Lea [0-9]* CHF, base [0-9]*" "$S" | grep -o "base [0-9]*" | head -1)
tom=$(grep -o "paid Tom [0-9]* CHF, base [0-9]*" "$S" | grep -o "base [0-9]*" | head -1)
echo "[versteckisnetcheck] pay: Lea $lea, Tom $tom"
if tc_ok 2 "$OUT/versteckisnet_A.log" "$OUT/versteckisnet_B.log" && grep -q "\[vs\] withheld Tom" "$S" \
   && grep -q "left the church door in Hide: put back" "$S" && grep -q "tag refused" "$S" && grep -q "missed at" "$S" \
   && grep -q "caught Tom" "$S" && grep -q "completed the bell" "$S" && [ -n "$lea" ] && [ "$lea" = "$tom" ]; then
  echo "[versteckisnetcheck] RESULT: ok"; exit 0
fi
echo "[versteckisnetcheck] RESULT: FAILED (see $OUT/versteckisnet_*.log)"; exit 1
