#!/usr/bin/env bash
# A cinema's booth over loopback (#1238, src/Interiors/CinemaNetProbe): a headless dedicated server on
# the GENERATED world and three headless clients at a village. A walks into a house and asks for the
# lights off and the cat on (the booth's rule is "only from inside", whatever the building); B, outside,
# must see it arrive and be refused when it asks the opposite; C joins only after A's ask and must find
# the state from the table the server sends a newcomer.
#   GODOT=<exe> [PORT=] [AT=E,N] tools/cinemanetcheck.sh      output in test_output/cinemanet_*.log
. "$(dirname "$0")/lib/twoclient.sh" cinemanet
PORT=${PORT:-7947}
# a generated village with houses round it (the site office check's village)
AT=${AT:-2586561,1115543}
# the generated world wherever it runs: no real tiles, even where terrain_location.json has some (#943)
WORLD="--generated-world ${NOTERRAIN[*]} --traffic 0 --at $AT"
# C starts on A's "set" line: never on an earlier run's log
rm -f "$OUT"/cinemanet_A.log "$OUT"/cinemanet_B.log "$OUT"/cinemanet_C.log
tc_server 420 120 "$OUT/cinemanet_server.log" --server --port $PORT --generated-world "${NOTERRAIN[@]}"
tc_client 360 "$OUT/cinemanet_A.log" --connect 127.0.0.1:$PORT --name CinemaA $WORLD --cinemanet A & A=$!
tc_client 360 "$OUT/cinemanet_B.log" --connect 127.0.0.1:$PORT --name CinemaB $WORLD --cinemanet B & B=$!
# C joins late: once A's ask was granted
for i in $(seq 1 600); do
  grep -q "\[cinema A\] set" "$OUT/cinemanet_A.log" 2>/dev/null && break
  kill -0 $A 2>/dev/null || break
  guard_nap 0.5
done
tc_client 300 "$OUT/cinemanet_C.log" --connect 127.0.0.1:$PORT --name CinemaC $WORLD --cinemanet C & C=$!
wait $A $B $C
tc_stop
grep -ah "\[cinema" "$OUT/cinemanet_A.log" "$OUT/cinemanet_B.log" "$OUT/cinemanet_C.log"
if tc_ok 3 "$OUT/cinemanet_A.log" "$OUT/cinemanet_B.log" "$OUT/cinemanet_C.log"; then echo "[cinemanetcheck] RESULT: ok"; exit 0; fi
echo "[cinemanetcheck] RESULT: FAILED (see $OUT/cinemanet_*.log)"; exit 1
