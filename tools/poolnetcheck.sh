#!/usr/bin/env bash
# Pool over loopback (#1324, src/Interiors/Games/Pool/PoolNetProbe): a headless dedicated server on the REAL map at
# BowlinGland (Gland) and two headless clients. A and B take the two seats of a pool table and shoot in turn, the bot's
# way; after every shot both compare the table to the millimetre, and each peer's rig rests where the server has the
# balls. B steps back; A adds a bot, which the server plays. Needs the real tiles (no venues in the generated world):
#   GODOT=<exe> CHUNKS=D:/UnitSportSwitzerland/terrain_chunks [PORT=] tools/poolnetcheck.sh    output in test_output/poolnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" poolnet
PORT=${PORT:-7954}
if [ -z "${CHUNKS:-}" ] || [ ! -d "$CHUNKS" ]; then echo "[poolnetcheck] no CHUNKS with the real map: skipped"; echo "[poolnetcheck] RESULT: ok (skipped)"; exit 0; fi
# BowlinGland's side door on the lanes' wall (its main door reads as one with no doorway)
AT=${AT:-2510427,1140980}
DOOR=${DOOR:-2510_1140_26_2}
WORLD="--chunks $CHUNKS --origin 2510000,1140000 --traffic 0 --at $AT --pooldoor $DOOR"
rm -f "$OUT"/poolnet_A.log "$OUT"/poolnet_B.log
tc_server 1200 150 "$OUT/poolnet_server.log" --server --port $PORT --chunks "$CHUNKS" --origin 2510000,1140000
tc_client 1100 "$OUT/poolnet_A.log" --connect 127.0.0.1:$PORT --name PoolNetA $WORLD --poolnet A & A=$!
tc_client 1100 "$OUT/poolnet_B.log" --connect 127.0.0.1:$PORT --name PoolNetB $WORLD --poolnet B & B=$!
wait $A $B
tc_stop
grep -ah "\[poolnet\|\[games\]" "$OUT/poolnet_server.log" "$OUT/poolnet_A.log" "$OUT/poolnet_B.log" | head -60
if tc_ok 2 "$OUT/poolnet_A.log" "$OUT/poolnet_B.log"; then echo "[poolnetcheck] RESULT: ok"; exit 0; fi
echo "[poolnetcheck] RESULT: FAILED (see $OUT/poolnet_*.log)"; exit 1
