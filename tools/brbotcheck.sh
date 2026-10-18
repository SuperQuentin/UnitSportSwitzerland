#!/usr/bin/env bash
# One player against bots in a whole Battle Royale match (src/BattleRoyale/BrBotProbe, #837), on a generated
# world at 0.3 of the normal pace (bots land within 100 m of the centre): /br bots 3 tops the match up with two bots, alone in the lobby the countdown
# starts, A knifes both bots (their death boxes, the feed's [bot] marks, "1 kill (1 bot)"), wins with
# "2 kills (2 bots)" in its record and on /br top, and the bots go with the match.
#   GODOT=<exe> tools/brbotcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" brbots
AT=2583250,1113250
PORT=${PORT:-7835}
tc_server 400 120 "$OUT/brbots_server.log" --server --generated-world --port $PORT --no-query --admin-password brbots --brpace 0.3 --brbotspread 100 --brbotsharmless
tc_client 300 "$OUT/brbots_A.log" --connect 127.0.0.1:$PORT --name "BRBA" --cache "$OUT/brbots_cache_A" --at "$AT" --view first --brbotcheck
tc_stop
grep -h "\[brbots" "$OUT/brbots_A.log" | grep -v " say "
grep -h "^\[br\]\|^\[bots\]" "$OUT/brbots_server.log" | head -30
if tc_ok 1 "$OUT"/brbots_A.log; then
    echo "[brbotcheck] RESULT: ok"; exit 0
fi
echo "[brbotcheck] RESULT: FAILED (see $OUT/brbots_*.log)"; exit 1
