#!/usr/bin/env bash
# One whole Battle Royale match over loopback (src/BattleRoyale/BrProbe, #183), on a generated world at 1/20 of
# the normal pace: A opens and starts it, both drop in, B is hurt by the zone, A knifes B, B spectates, A wins,
# both go back where they started with their own packs. Plus the headless --brcheck self-test.
#   GODOT=<exe> tools/brcheck.sh   [CHUNKS=<terrain_chunks dir> from a worktree, for --brcheck's real regions]
#   screenshots: test_output/br_{a,b}_{dropped,results}.png, br_b_outside.png
set -u
AT=2583250,1113250
AT_B=2583256,1113250   # never on top of A: two bodies spawned in one place throw each other kilometres
PORT=7799
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
CH=(); [ -n "${CHUNKS:-}" ] && CH=(--chunks "$CHUNKS")
timeout 200 "$GODOT" --headless --path . -- "${CH[@]}" --brcheck > "$OUT/br_selfcheck.log" 2>&1
grep -h "\[brcheck\]" "$OUT/br_selfcheck.log"

# REAL=1 (with CHUNKS): the match on real Swiss terrain, a random region anywhere, instead of a generated world
WORLD=(--generated-world); [ -n "${REAL:-}" ] && WORLD=("${CH[@]}")
timeout 400 "$GODOT" --headless --path . -- --server "${WORLD[@]}" --port $PORT --admin-password brcheck --brpace 0.05 \
    > "$OUT/br_server.log" 2>&1 & SERVER=$!
trap 'kill $SERVER 2>/dev/null' EXIT
sleep ${SERVER_WAIT:-12}
client() { timeout 300 "$GODOT" --path . -- "${CH[@]}" --connect 127.0.0.1:$PORT --name "BR$1" --cache "$OUT/br_cache_$1" \
    --at "$2" --brprobe "$1" > "$OUT/br_$1.log" 2>&1; }
client A "$AT" & A=$!
client B "$AT_B"
wait $A
kill $SERVER 2>/dev/null
grep -h "\[br " "$OUT"/br_A.log "$OUT"/br_B.log
grep -h "^\[br\]" "$OUT/br_server.log"
if grep -q "RESULT PASS" "$OUT/br_selfcheck.log" && [ "$(grep -h "RESULT: ok" "$OUT"/br_A.log "$OUT"/br_B.log | wc -l)" -eq 2 ]; then
    echo "[brcheck] RESULT: ok"; exit 0
fi
echo "[brcheck] RESULT: FAILED (see $OUT/br_*.log)"; exit 1
