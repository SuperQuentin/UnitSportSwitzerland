#!/usr/bin/env bash
# Fahnenschwingen over loopback (#1269 part 5, src/Occasions/Festivals/Fahnen/FahnenNetProbe.cs): a dedicated server on the fixture, its calendar
# read as 16 August 2027 (day 3 of the Alplerfest, held at the fixture start under --festival test). Everything is checked on the REMOTE peers:
# Lea and Tom stand on the flag ground and ask for a routine in the same slot; each is told its start and seed, plays the whole routine through the
# real game, hands it in, and is told its result: in step with each other (x1.3), paid Batzen that arrive in their own section, their points on
# their board. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/fahnenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" fahnenet
PORT=${PORT:-7971}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-08-16 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/fahnenet_$f.log"; done
B_UD="$OUT/userdata_fahnenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 500 120 "$OUT/fahnenet_server.log" "${SARGS[@]}"
tc_client 400 "$OUT/fahnenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --fahnenet a &
A=$!
( tc_userdata "$B_UD"; tc_client 400 "$OUT/fahnenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --fahnenet b ) &
B=$!
wait $A $B
tc_stop
S="$OUT/fahnenet_server.log"
grep -h "swung the flag" "$S"
grep -h "\[fahnenet" "$OUT"/fahnenet_A.log "$OUT"/fahnenet_B.log
swung=$(grep -c "swung the flag" "$S")
if tc_ok 2 "$OUT/fahnenet_A.log" "$OUT/fahnenet_B.log" && [ "$swung" = 2 ]; then
  echo "[fahnenetcheck] RESULT: ok"; exit 0
fi
echo "[fahnenetcheck] RESULT: FAILED (see $OUT/fahnenet_*.log; $swung payments on the server)"; exit 1
