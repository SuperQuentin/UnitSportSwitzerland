#!/usr/bin/env bash
# One player against bots in a whole Battle Royale match (src/BattleRoyale/BrBotProbe, #837), on a generated
# world at the real short pace (bots land within 100 m of the centre): /br bots 3 tops the match up with two bots, alone in the lobby the countdown
# starts, A knifes both bots (their death boxes, the feed's [bot] marks, "1 kill (1 bot)"), wins with
# "2 kills (2 bots)" in its record and on /br top, and the bots go with the match.
# Not a test pace (#873): the bots spend 1.5-2.5 min falling and under their canopies, and at --brpace 0.3 the zone
# was in its third or fourth circle by then, finishing a bot (or A) before the knife did. At the short pace the
# first shrink ends 216 s after the doors close and its circle holds every landing spot, so the zone never takes part.
# The seed is fixed (--brseed): the same region, zone, plane line and bots every run. BRSEED=0 draws one, BRPACE,
# BOTLOG=1 (bot status every 5 s) and SERVER_ARGS go to the server.
# At /speed 4 (A's --checkspeed, #931), unlike a faster --brpace: the falls, the zone and the probe's waits all
# scale together, so the zone still never takes part. SPEED=1 runs it at normal speed.
#   GODOT=<exe> tools/brbotcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" brbots
AT=2583250,1113250
PORT=${PORT:-7835}
tc_server 560 120 "$OUT/brbots_server.log" --server --generated-world --port $PORT --no-query --admin-password brbots --brpace ${BRPACE:-1} --brbotspread 100 --brbotsharmless --brseed ${BRSEED:-1152803998} ${BOTLOG:+--botlog} ${SERVER_ARGS:-}
tc_client 440 "$OUT/brbots_A.log" --connect 127.0.0.1:$PORT --name "BRBA" --cache "$OUT/brbots_cache_A" --at "$AT" --view first --brbotcheck --checkspeed ${SPEED:-4}
tc_stop
grep -h "\[brbots" "$OUT/brbots_A.log" | grep -v " say "
grep -h "^\[br\]\|^\[bots\]" "$OUT/brbots_server.log" | head -30
if tc_ok 1 "$OUT"/brbots_A.log; then
    echo "[brbotcheck] RESULT: ok"; exit 0
fi
echo "[brbotcheck] RESULT: FAILED (see $OUT/brbots_*.log)"; exit 1
