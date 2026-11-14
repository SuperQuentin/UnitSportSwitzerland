#!/usr/bin/env bash
# A bot goes into a building on a dedicated server and comes back out, seen from two clients (src/Bots/BotIndoorProbe,
# #1330): the server (--generated-world) runs one bot with --botsindoors beside a generated village, so its every goal is
# a visit. The server builds the interior's collision in the bot's region; each client sees the bot's space become the
# building's, its body indoors and standing, walking inside, then the street again.
#   GODOT=<exe> tools/botindoorcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" botindoor
AT=${AT:-2586561,1115543}   # a generated village with houses, a church and a building site (interiorcheck's)
AT_B="$(( ${AT%,*} + 12 )),${AT#*,}"
PORT=7838
tc_server 600 120 "$OUT/botindoor_server.log" --server --generated-world --port $PORT --no-query --admin-password botindoor \
    --bots 1 --botsat "$AT" --botsindoors --botlog
client() { tc_client 540 "$OUT/botindoor_$1.log" --connect 127.0.0.1:$PORT --name "Bi$1" --cache "$OUT/botindoor_cache_$1" \
    --at "$2" --view first --botindoorcheck "$1" ${CLIENT_ARGS:-}; }
client A "$AT" & A=$!
client B "$AT_B"
wait $A
tc_stop
grep -ha "\[botindoor [AB]\]" "$OUT/botindoor_A.log" "$OUT/botindoor_B.log"
grep -h "\[interior\] bots:\|visit" "$OUT/botindoor_server.log" | head -20
if tc_ok 2 "$OUT"/botindoor_A.log "$OUT"/botindoor_B.log; then
    echo "[botindoorcheck] RESULT: ok"; exit 0
fi
echo "[botindoorcheck] RESULT: FAILED (see $OUT/botindoor_*.log)"; exit 1
