#!/usr/bin/env bash
# The Christmas market over loopback (#1348, src/Occasions/Festivals/Christmas/ChristmasNetProbe.cs): a dedicated server on the fixture, its calendar read as
# 30 November 2027 (day 6 of 29, held at the fixture start under --festival test). Everything is checked on the REMOTE peers: Lea tastes at the Gluehwein
# stall (paid in her own section, the stamp comes back over the wire), buys the mug (it arrives in her pack), drinks it (the server logs 15.8 g and it reaches
# her own progress, she is warm); Tom, who never drank, tastes at the same stall with a stamp of his own, is refused a stall and the Samichlaus from 150 m
# and the Samichlaus's bag before 6 December, and buys a mug of his own. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/christmasnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" christmasnet
PORT=${PORT:-7974}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-11-30 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/christmasnet_$f.log"; done
B_UD="$OUT/userdata_christmasnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/christmasnet_server.log" "${SARGS[@]}"
tc_client 400 "$OUT/christmasnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --christmasnet a &
A=$!
( tc_userdata "$B_UD"; tc_client 400 "$OUT/christmasnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --christmasnet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[christmasnet] the server was still up when the clients ended" || echo "[christmasnet] the server had stopped before the clients ended"
tc_stop
S="$OUT/christmasnet_server.log"
grep -h "bought a\|tasted at\|drinks 15" "$S"
grep -h "\[christmasnet" "$OUT"/christmasnet_A.log "$OUT"/christmasnet_B.log
bought=$(grep -c "bought a Gl" "$S")
tasted=$(grep -c "tasted at the Gl" "$S")
if tc_ok 2 "$OUT/christmasnet_A.log" "$OUT/christmasnet_B.log" && [ "$bought" = 2 ] && [ "$tasted" = 2 ]; then
  echo "[christmasnetcheck] RESULT: ok"; exit 0
fi
echo "[christmasnetcheck] RESULT: FAILED (see $OUT/christmasnet_*.log; $bought mugs sold and $tasted tastes on the server, want 2 and 2)"; exit 1
