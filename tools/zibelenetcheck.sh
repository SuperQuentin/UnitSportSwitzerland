#!/usr/bin/env bash
# The Zibelemaerit over loopback (#1373, src/Occasions/Festivals/Zibelemaerit/ZibeleNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 22 November 2027 (the market's one day, held at the fixture start under --festival test). Everything is checked on the REMOTE peers: Lea tastes at the Konfetti
# stall (paid in her own section, the stamp comes back over the wire), buys six confetti eggs (they arrive in her pack), throws five with the item's Use (the
# server counts them from her position, the fifth pays and reaches her own section); Tom tastes at the braid stall with a stamp of his own, is refused a stall
# from 150 m, and buys a braid of his own. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/zibelenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" zibelenet
PORT=${PORT:-7975}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-11-22 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/zibelenet_$f.log"; done
B_UD="$OUT/userdata_zibelenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/zibelenet_server.log" "${SARGS[@]}"
tc_client 400 "$OUT/zibelenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --zibelenet a &
A=$!
( tc_userdata "$B_UD"; tc_client 400 "$OUT/zibelenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --zibelenet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[zibelenet] the server was still up when the clients ended" || echo "[zibelenet] the server had stopped before the clients ended"
tc_stop
S="$OUT/zibelenet_server.log"
grep -h "bought \|tasted at\|five throws" "$S"
grep -h "\[zibelenet" "$OUT"/zibelenet_A.log "$OUT"/zibelenet_B.log
bought=$(grep -c "bought " "$S")
tasted=$(grep -c "tasted at the" "$S")
if tc_ok 2 "$OUT/zibelenet_A.log" "$OUT/zibelenet_B.log" && [ "$bought" = 2 ] && [ "$tasted" = 2 ]; then
  echo "[zibelenetcheck] RESULT: ok"; exit 0
fi
echo "[zibelenetcheck] RESULT: FAILED (see $OUT/zibelenet_*.log; $bought wares sold and $tasted tastes on the server, want 2 and 2)"; exit 1
