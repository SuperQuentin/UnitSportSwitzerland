#!/usr/bin/env bash
# The 1 August over loopback (#1355, src/Occasions/Festivals/August1/August1NetProbe.cs): a dedicated server on the fixture, its calendar read as 31 July 2027
# (day 1 of 2, held at the fixture start under --festival test) and its sky as 22:30 (--august1hour). Everything is checked on the REMOTE peers: both clients see
# the lake show on the server's books, play it from the server's seed and are paid for watching it; Tom is refused the stand from 150 m; both take a lampion and
# meet at the post; Lea starts the lampion walk (two people and a village child); at the first stop Tom is left behind and the server makes the party wait; they
# walk all ten stops together and both are paid for two walkers in their own sections; Tom buys a box of fireworks and lights it, Lea plays his volley from the
# seed the server chose; Lea sees the party's points on the shared board. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/august1netcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" august1net
PORT=${PORT:-7977}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-07-31 --august1fast --august1hour 22.5 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/august1net_$f.log"; done
B_UD="$OUT/userdata_august1net_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/august1net_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/august1net_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --august1net a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/august1net_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --august1net b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[august1net] the server was still up when the clients ended" || echo "[august1net] the server had stopped before the clients ended"
tc_stop
S="$OUT/august1net_server.log"
grep -h "the lampion walk with\|watched the lake fireworks\|bought a box of fireworks" "$S"
grep -h "\[august1net" "$OUT"/august1net_A.log "$OUT"/august1net_B.log
walks=$(grep -c "the lampion walk with 2" "$S")
watched=$(grep -c "watched the lake fireworks" "$S")
boxes=$(grep -c "bought a box of fireworks" "$S")
if tc_ok 2 "$OUT/august1net_A.log" "$OUT/august1net_B.log" && [ "$walks" = 2 ] && [ "$watched" = 2 ] && [ "$boxes" = 1 ]; then
  echo "[august1netcheck] RESULT: ok"; exit 0
fi
echo "[august1netcheck] RESULT: FAILED (see $OUT/august1net_*.log; $walks walk payments, $watched watch payments and $boxes boxes on the server, want 2, 2 and 1)"; exit 1
