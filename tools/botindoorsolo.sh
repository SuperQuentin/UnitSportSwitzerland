#!/usr/bin/env bash
# Bots offline (src/Bots/BotIndoorSolo): single-player has the server's bot system, and a bot goes into a building and
# back out with no server at all, on the path tools/botindoorcheck.sh checks on a dedicated server. One offline game on
# the generated world, one bot with --botsindoors beside a generated village.
#   GODOT=<exe> tools/botindoorsolo.sh
# SHOTS=<dir>: in a window, the player keeps 5 m behind the bot and its last frames before it steps through the door are
# saved as PNG (solo_in_NN.png); a summer noon without occasions.
. "$(dirname "$0")/lib/twoclient.sh" botindoorsolo
AT=${AT:-2586561,1115543}   # a generated village with houses, a church and a building site (interiorcheck's)
win=() shot=()
if [ -n "${SHOTS:-}" ]; then win=(--windowed); shot=(--botindoorshot "$SHOTS" --date 2027-07-15 --time 12 --occasion off); fi
tc_client 600 "$OUT/botindoorsolo.log" ${win[@]+"${win[@]}"} --generated on --at "$AT" --view first --traffic 0 \
    --bots 1 --botsat "$AT" --botsindoors --botlog --botindoorsolo ${shot[@]+"${shot[@]}"} ${CLIENT_ARGS:-}
grep -ha "\[botindoorsolo\]\|\[interior\] bots:\|visit route\|gives up" "$OUT/botindoorsolo.log" | head -30
if tc_ok 1 "$OUT/botindoorsolo.log"; then
    echo "[botindoorsolo] RESULT: ok"; exit 0
fi
echo "[botindoorsolo] RESULT: FAILED (see $OUT/botindoorsolo.log)"; exit 1
