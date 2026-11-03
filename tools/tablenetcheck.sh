#!/usr/bin/env bash
# Any turn game over loopback (#1324, src/Interiors/Games/TableNetProbe): a headless dedicated server on the REAL map
# at BowlinGland (Gland) and two headless clients at the same piece (GAME: a FurnitureType, DartBoard by default). They
# take the seats and play the bot's moves in turn, comparing the whole packed game after every change; B steps back; A
# adds a bot, which the server plays. Needs the real tiles (no venues in the generated world):
#   GODOT=<exe> CHUNKS=D:/UnitSportSwitzerland/terrain_chunks [GAME=DartBoard] [PORT=] tools/tablenetcheck.sh    output in test_output/tablenet_*.log
. "$(dirname "$0")/lib/twoclient.sh" tablenet
PORT=${PORT:-7955}
if [ -z "${CHUNKS:-}" ] || [ ! -d "$CHUNKS" ]; then echo "[tablenetcheck] no CHUNKS with the real map: skipped"; echo "[tablenetcheck] RESULT: ok (skipped)"; exit 0; fi
# BowlinGland's side door on the lanes' wall (its main door reads as one with no doorway)
AT=${AT:-2510427,1140980}
DOOR=${DOOR:-2510_1140_26_2}
WORLD="--chunks $CHUNKS --origin 2510000,1140000 --traffic 0 --at $AT --tabledoor $DOOR --tablegame ${GAME:-DartBoard}"
rm -f "$OUT"/tablenet_A.log "$OUT"/tablenet_B.log
tc_server 1200 150 "$OUT/tablenet_server.log" --server --port $PORT --chunks "$CHUNKS" --origin 2510000,1140000
tc_client 1100 "$OUT/tablenet_A.log" --connect 127.0.0.1:$PORT --name TableNetA $WORLD --tablenet A & A=$!
tc_client 1100 "$OUT/tablenet_B.log" --connect 127.0.0.1:$PORT --name TableNetB $WORLD --tablenet B & B=$!
wait $A $B
tc_stop
grep -ah "\[tablenet\|\[games\]" "$OUT/tablenet_server.log" "$OUT/tablenet_A.log" "$OUT/tablenet_B.log" | head -60
if tc_ok 2 "$OUT/tablenet_A.log" "$OUT/tablenet_B.log"; then echo "[tablenetcheck] RESULT: ok"; exit 0; fi
echo "[tablenetcheck] RESULT: FAILED (see $OUT/tablenet_*.log)"; exit 1
