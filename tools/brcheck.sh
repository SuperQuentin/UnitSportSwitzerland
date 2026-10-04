#!/usr/bin/env bash
# One whole Battle Royale match over loopback (src/BattleRoyale/BrProbe, #183), on a generated world at 1/12 of
# the normal pace: A opens and starts it, B joins by itself (--br), both board the plane and jump, B is hurt by the zone, A knifes B, B spectates, A wins,
# both go back where they started with their own packs. Plus the headless --brcheck self-test.
#   GODOT=<exe> tools/brcheck.sh   [CHUNKS=<terrain_chunks dir> from a worktree, for --brcheck's real regions]
#   screenshots: test_output/br_{a,b}_{dropped,results}.png, br_b_outside.png
. "$(dirname "$0")/lib/twoclient.sh" br
AT=2583250,1113250
AT_B=2583256,1113250   # never on top of A: two bodies spawned in one place throw each other kilometres
PORT=${PORT:-7799}
tc_godot 200 "$OUT/br_selfcheck.log" --headless --path . -- ${CH[@]+"${CH[@]}"} --brcheck
grep -h "\[brcheck\]" "$OUT/br_selfcheck.log"

# REAL=1 (with CHUNKS): the match on real Swiss terrain, a random region anywhere, instead of a generated world.
# SITES=1: also the outdoor sites (crack a bunker, shoot a crate open, fire a flare); wants REAL=1 and BRPACE=0.2.
# SQUAD=1 (#469): instead of the duel, a duos match: both pick team "alp" in the lobby, A pings the ground and the
# map, B must see both; then B is downed and revived (#475), goes out and is recalled with its dogtag at a
# Postauto stop (#480), and the team is wiped. Runs at the normal pace (--brpace 1) so the recall comes well before zone 4.
WORLD=(--generated-world); [ -n "${REAL:-}" ] && WORLD=(${CH[@]+"${CH[@]}"})
# SERVER_WAIT: the longest wait for the server to listen
tc_server 400 "${SERVER_WAIT:-120}" "$OUT/br_server.log" --server ${WORLD[@]+"${WORLD[@]}"} --port $PORT --admin-password brcheck --brpace ${BRPACE:-$([ -n "${SQUAD:-}" ] && echo 1 || echo 0.13)}
client() { tc_client 300 "$OUT/br_$1.log" --windowed ${CH[@]+"${CH[@]}"} --connect 127.0.0.1:$PORT --name "BR$1" --cache "$OUT/br_cache_$1" \
    --at "$2" --brprobe "$1" ${SITES:+--brsites} ${SQUAD:+--brsquad} ${3:-}; }
client A "$AT" & A=$!
client B "$AT_B" --br
wait $A
tc_stop
grep -h "\[br " "$OUT"/br_A.log "$OUT"/br_B.log
grep -h "^\[br\]" "$OUT/br_server.log"
if grep -q "RESULT PASS" "$OUT/br_selfcheck.log" && tc_ok 2 "$OUT"/br_A.log "$OUT"/br_B.log; then
    echo "[brcheck] RESULT: ok"; exit 0
fi
echo "[brcheck] RESULT: FAILED (see $OUT/br_*.log)"; exit 1
