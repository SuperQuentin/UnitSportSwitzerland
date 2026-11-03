#!/usr/bin/env bash
# Versteckis with bots (#1268, src/Bots/VersteckisBrain + VersteckisBotProbe): one human and five bots, three rounds on
# the fixture hamlet, at /speed 4 (SPEED=). The human opens and starts, seeks round 1 without tagging and hides in the
# others. Checked: a bot seeker catches a bot hider, a bot hider completes an objective, every bot hides inside the
# circle (the round-end pins), the human is paid. BOTLOG=1 logs every bot's status every 5 s.
#   GODOT=<exe> tools/versteckisbotcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" versteckisbot
PORT=${PORT:-7943}
WORLD="--world fixture --chunks fixture:hamlet --pois fixture"
tc_server 700 120 "$OUT/versteckisbot_server.log" --server --port $PORT $WORLD --systems network,adventure --admin-password vspw ${BOTLOG:+--botlog}
tc_client 640 "$OUT/versteckisbot_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD --systems network,physics,ui,adventure --vsbot --checkspeed ${SPEED:-4}
tc_stop
grep -h "\[vsbot" "$OUT/versteckisbot_A.log"
grep -h "^\[vs\]" "$OUT/versteckisbot_server.log" | grep -v "withheld\|fails the" | head -60
if tc_ok 1 "$OUT/versteckisbot_A.log" && grep -q "paid Lea" "$OUT/versteckisbot_server.log"; then
  echo "[versteckisbotcheck] RESULT: ok"; exit 0
fi
echo "[versteckisbotcheck] RESULT: FAILED (see $OUT/versteckisbot_*.log)"; exit 1
