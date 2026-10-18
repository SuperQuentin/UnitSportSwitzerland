#!/usr/bin/env bash
# Films bots playing a Battle Royale match (src/Bots/BotFilmProbe, #842): a dedicated server (--generated-world,
# pace 0.5, bots landing within 120 m of the centre) and one windowed client that opens the match, leaves it
# and spectates the bots. Two clips: test_output/clips/bots-drop.{mp4,gif} and bots-fight.{mp4,gif}.
#   GODOT=<exe> [CLIENT_ARGS="--screen 1"] tools/botfilm.sh
. "$(dirname "$0")/lib/twoclient.sh" botfilm
AT=2583250,1113250
PORT=${PORT:-7833}
tc_server 900 120 "$OUT/botfilm_server.log" --server --generated-world --port $PORT --no-query --admin-password botfilm \
    --brpace ${BRPACE:-0.5} --brbotspread ${SPREAD:-120}
tc_client 800 "$OUT/botfilm_A.log" --windowed --resolution 1280x720 --connect 127.0.0.1:$PORT --name "Film" \
    --cache "$OUT/botfilm_cache_A" --at "$AT" --botfilm ${CLIENT_ARGS:-}
tc_stop
grep -h "\[botfilm\|\[clip\]" "$OUT/botfilm_A.log"
ls -la "$OUT"/clips/bots-*.* 2>/dev/null
if tc_ok 1 "$OUT"/botfilm_A.log; then echo "[botfilm] RESULT: ok"; exit 0; fi
echo "[botfilm] RESULT: FAILED (see $OUT/botfilm_*.log)"; exit 1
