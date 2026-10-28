#!/usr/bin/env bash
# Pinball over loopback (#1325, src/Interiors/Pinball/PinballNetProbe): a headless dedicated server on the REAL map at
# BowlinGland (Gland) and two headless clients. A walks in and plays a machine (a robot at its flippers, its client
# runs the table); B watches A's balls streamed, presses Start in the first ball and joins as player 2; the server
# hands the table to B's client at A's drain (B runs it from the game's log); at the game's end both read the same
# scores, the ones the last runner's own rules counted. Needs the real tiles (no venues in the generated world):
#   GODOT=<exe> CHUNKS=D:/UnitSportSwitzerland/terrain_chunks [PORT=] tools/pinballnetcheck.sh    output in test_output/pinnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" pinnet
PORT=${PORT:-7953}
if [ -z "${CHUNKS:-}" ] || [ ! -d "$CHUNKS" ]; then echo "[pinballnetcheck] no CHUNKS with the real map: skipped"; echo "[pinballnetcheck] RESULT: ok (skipped)"; exit 0; fi
# BowlinGland's side door on the lanes' wall (its main door reads as one with no doorway)
AT=${AT:-2510427,1140980}
DOOR=${DOOR:-2510_1140_26_2}
WORLD="--chunks $CHUNKS --origin 2510000,1140000 --traffic 0 --at $AT --pinballdoor $DOOR"
rm -f "$OUT"/pinnet_A.log "$OUT"/pinnet_B.log
tc_server 1200 150 "$OUT/pinnet_server.log" --server --port $PORT --chunks "$CHUNKS" --origin 2510000,1140000
tc_client 1100 "$OUT/pinnet_A.log" --connect 127.0.0.1:$PORT --name PinNetA $WORLD --pinballnet A & A=$!
tc_client 1100 "$OUT/pinnet_B.log" --connect 127.0.0.1:$PORT --name PinNetB $WORLD --pinballnet B & B=$!
wait $A $B
tc_stop
grep -ah "\[pinnet\|\[pinball\]" "$OUT/pinnet_server.log" "$OUT/pinnet_A.log" "$OUT/pinnet_B.log" | head -60
if tc_ok 2 "$OUT/pinnet_A.log" "$OUT/pinnet_B.log"; then echo "[pinballnetcheck] RESULT: ok"; exit 0; fi
echo "[pinballnetcheck] RESULT: FAILED (see $OUT/pinnet_*.log)"; exit 1
