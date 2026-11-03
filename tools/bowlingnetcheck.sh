#!/usr/bin/env bash
# Bowling over loopback (#1323, src/Interiors/Bowling/BowlingNetProbe): a headless dedicated server on the REAL
# map at BowlinGland (Gland) and three headless clients. A and B walk in, sign in at the shoe counter (one lane),
# take a ball and step onto the approach; A rolls, the server runs the deck; A and B must agree on the pins,
# B must have had the deck's frames to replay and be refused a roll out of turn. C walks in after A's roll and
# must find the same score and standing pins. Needs the real tiles (no venues in the generated world):
#   GODOT=<exe> CHUNKS=D:/UnitSportSwitzerland/terrain_chunks [PORT=] tools/bowlingnetcheck.sh    output in test_output/bowlnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" bowlnet
PORT=${PORT:-7951}
if [ -z "${CHUNKS:-}" ] || [ ! -d "$CHUNKS" ]; then echo "[bowlingnetcheck] no CHUNKS with the real map: skipped"; echo "[bowlingnetcheck] RESULT: ok (skipped)"; exit 0; fi
# BowlinGland's side door on the lanes' wall (its main door reads as one with no doorway)
AT=${AT:-2510427,1140980}
DOOR=${DOOR:-2510_1140_26_2}
WORLD="--chunks $CHUNKS --origin 2510000,1140000 --traffic 0 --at $AT --bowlingdoor $DOOR"
rm -f "$OUT"/bowlnet_A.log "$OUT"/bowlnet_B.log "$OUT"/bowlnet_C.log
tc_server 600 150 "$OUT/bowlnet_server.log" --server --port $PORT --chunks "$CHUNKS" --origin 2510000,1140000
tc_client 540 "$OUT/bowlnet_A.log" --connect 127.0.0.1:$PORT --name BowlNetA $WORLD --bowlingnet A & A=$!
tc_client 540 "$OUT/bowlnet_B.log" --connect 127.0.0.1:$PORT --name BowlNetB $WORLD --bowlingnet B & B=$!
# C joins late: once A has rolled
for i in $(seq 1 900); do
  grep -q "\[bowlnet A\] rolled" "$OUT/bowlnet_A.log" 2>/dev/null && break
  kill -0 $A 2>/dev/null || break
  guard_nap 0.5
done
tc_client 360 "$OUT/bowlnet_C.log" --connect 127.0.0.1:$PORT --name BowlNetC $WORLD --bowlingnet C & C=$!
wait $A $B $C
tc_stop
grep -ah "\[bowlnet\|\[bowling\]" "$OUT/bowlnet_server.log" "$OUT/bowlnet_A.log" "$OUT/bowlnet_B.log" "$OUT/bowlnet_C.log" | head -60
if tc_ok 3 "$OUT/bowlnet_A.log" "$OUT/bowlnet_B.log" "$OUT/bowlnet_C.log"; then echo "[bowlingnetcheck] RESULT: ok"; exit 0; fi
echo "[bowlingnetcheck] RESULT: FAILED (see $OUT/bowlnet_*.log)"; exit 1
