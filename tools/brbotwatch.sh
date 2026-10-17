#!/usr/bin/env bash
# One player watches a whole Battle Royale match played by bots (src/BattleRoyale/BrBotProbe --brbotwatch, #837
# phase 2): /br bots 9 tops it up with eight bots that drop, loot, fight, heal and run from the zone; the match
# must end, bots must have dealt damage and killed. Pace 0.13 (a few minutes). Server log: [bots] goals and takes.
#   GODOT=<exe> [BRPACE=1] [SPREAD=80: land the bots within 80 m of the centre] [BOTLOG=1] tools/brbotwatch.sh [WINDOWED=1 CLIENT_ARGS="--screen 1"]
. "$(dirname "$0")/lib/twoclient.sh" brbotwatch
AT=2583250,1113250
PORT=${PORT:-7834}
tc_server 2400 120 "$OUT/brbotwatch_server.log" --server --generated-world --port $PORT --no-query --admin-password brbots --brpace ${BRPACE:-0.13} ${BOTLOG:+--botlog} ${SPREAD:+--brbotspread $SPREAD} ${SERVER_ARGS:-}
tc_client 2300 "$OUT/brbotwatch_A.log" --connect 127.0.0.1:$PORT --name "BRBA" --cache "$OUT/brbotwatch_cache_A" --at "$AT" --view first --brbotwatch ${CLIENT_ARGS:-}
tc_stop
grep -h "\[brbots" "$OUT/brbotwatch_A.log" | grep -v " say "
grep -h "^\[br\] \(out\|ended\|bots\|go\)" "$OUT/brbotwatch_server.log" | head -40
if tc_ok 1 "$OUT"/brbotwatch_A.log; then echo "[brbotwatch] RESULT: ok"; exit 0; fi
echo "[brbotwatch] RESULT: FAILED (see $OUT/brbotwatch_*.log)"; exit 1
