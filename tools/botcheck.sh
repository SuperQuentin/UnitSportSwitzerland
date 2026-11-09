#!/usr/bin/env bash
# Bots the server runs, seen from two clients (src/Bots/BotProbe, #837): a dedicated server
# (--generated-world) with 3 bots round the spawn. A sees them marked, sees one walk on the server's
# collision, walks up to it and shoots it down with a pistol (the server takes the hits itself), then
# sees it stand up again (free roam knocks out); B sees the same bot, and sees it go down.
#   GODOT=<exe> [WINDOWED=1 CLIENT_ARGS="--screen 1"] tools/botcheck.sh   screenshots: test_output/bots_a_{bots,down}.png (windowed)
# EARTH=1 plays it under --frame earth on built leaves (#1513, G2 C4): the server's frame ENU on EarthWorld, its bots on
# the leaves' collision patches, each client in an ENU frame of its own on the same leaves (default Geneva).
#   EARTH=1 [LEAVES=<dir>] [CHUNKS=<dir>] [AT=E,N] tools/botcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" bots
AT=2583250,1113250
WORLD=(--generated-world)
CWORLD=()
if [ -n "${EARTH:-}" ]; then
  LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
  CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}
  AT=${AT:-2500956,1117569}   # Geneva, the densest leaf of the D: region
  if [ ! -d "$LEAVES" ]; then echo "[botcheck] no LEAVES with built leaves: skipped"; echo "[botcheck] RESULT: ok (skipped)"; exit 0; fi
  WORLD=(--frame earth --leaves "$LEAVES" --chunks "$CHUNKS")
  CWORLD=("${WORLD[@]}")
fi
AT_B="$(( ${AT%,*} + 12 )),${AT#*,}"   # never on top of A
PORT=7836
tc_server 560 120 "$OUT/bots_server.log" --server "${WORLD[@]}" --port $PORT --no-query --admin-password botcheck --bots 3 --botsat $AT --botlog
client() { tc_client 480 "$OUT/bots_$1.log" --connect 127.0.0.1:$PORT --name "Bot$1" --cache "$OUT/bots_cache_$1" \
    ${CWORLD[@]+"${CWORLD[@]}"} --at "$2" --view first --botcheck "$1" ${CLIENT_ARGS:-}; }
client A "$AT" & A=$!
client B "$AT_B"
wait $A
tc_stop
grep -h "\[bots" "$OUT/bots_A.log" "$OUT/bots_B.log" | grep -v " say "
grep -h "\[bots\] \(spawned\|removed\)\|\[pvp\] peer" "$OUT/bots_server.log" | head -20
if tc_ok 2 "$OUT"/bots_A.log "$OUT"/bots_B.log; then
    echo "[botcheck] RESULT: ok"; exit 0
fi
echo "[botcheck] RESULT: FAILED (see $OUT/bots_*.log)"; exit 1
