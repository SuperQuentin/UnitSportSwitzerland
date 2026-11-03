#!/usr/bin/env bash
# Any table game over loopback (#1324, #1326, src/Interiors/Games/TableNetProbe): a headless dedicated server and two
# headless clients at the same piece (GAME: a FurnitureType, DartBoard by default). Turn games: they take the seats and
# play the bot's moves in turn, comparing the whole packed game after every change. Live games: each pushes and the
# other sees it move. A one-player machine (the claw): A plays a go, B watches. Then B steps back and A adds a bot.
# Where: the real map at BowlinGland by a door (default), a real venue by name (VENUE=, with ORIGIN= and AT=), or a test
# world (TEST_WORLD=fun: Pixel Paradise's air hockey and claw machine, no real tiles):
#   GODOT=<exe> CHUNKS=D:/UnitSportSwitzerland/terrain_chunks [GAME=DartBoard] [PORT=] tools/tablenetcheck.sh
#   GODOT=<exe> CHUNKS=... GAME=TableFootball VENUE="Billard Bluff" ORIGIN=2501000,1117000 AT=2501440,1117640 tools/tablenetcheck.sh
#   GODOT=<exe> TEST_WORLD=fun GAME=ClawMachine VENUE="Pixel Paradise" tools/tablenetcheck.sh    output in test_output/tablenet_*.log
. "$(dirname "$0")/lib/twoclient.sh" tablenet
PORT=${PORT:-7955}
GAME=${GAME:-DartBoard}
if [ -n "${VENUE:-}" ]; then PLACE=(--tablevenue "$VENUE"); else PLACE=(--tabledoor "${DOOR:-2510_1140_26_2}"); fi
if [ -n "${TEST_WORLD:-}" ]; then
  SERVER_ARGS=(--test-world "$TEST_WORLD")
  WORLD=(--test-world "$TEST_WORLD" --tablegame "$GAME")
else
  if [ -z "${CHUNKS:-}" ] || [ ! -d "$CHUNKS" ]; then echo "[tablenetcheck] no CHUNKS with the real map: skipped"; echo "[tablenetcheck] RESULT: ok (skipped)"; exit 0; fi
  ORIGIN=${ORIGIN:-2510000,1140000}
  # BowlinGland's side door on the lanes' wall (its main door reads as one with no doorway)
  SERVER_ARGS=(--chunks "$CHUNKS" --origin "$ORIGIN")
  WORLD=(--chunks "$CHUNKS" --origin "$ORIGIN" --traffic 0 --at "${AT:-2510427,1140980}" --tablegame "$GAME")
fi
rm -f "$OUT"/tablenet_A.log "$OUT"/tablenet_B.log
tc_server 1200 150 "$OUT/tablenet_server.log" --server --port $PORT "${SERVER_ARGS[@]}"
tc_client 1100 "$OUT/tablenet_A.log" --connect 127.0.0.1:$PORT --name TableNetA "${WORLD[@]}" "${PLACE[@]}" --tablenet A & A=$!
tc_client 1100 "$OUT/tablenet_B.log" --connect 127.0.0.1:$PORT --name TableNetB "${WORLD[@]}" "${PLACE[@]}" --tablenet B & B=$!
wait $A $B
tc_stop
grep -ah "\[tablenet\|\[games\]" "$OUT/tablenet_server.log" "$OUT/tablenet_A.log" "$OUT/tablenet_B.log" | head -60
if tc_ok 2 "$OUT/tablenet_A.log" "$OUT/tablenet_B.log"; then echo "[tablenetcheck] RESULT: ok"; exit 0; fi
echo "[tablenetcheck] RESULT: FAILED (see $OUT/tablenet_*.log)"; exit 1
