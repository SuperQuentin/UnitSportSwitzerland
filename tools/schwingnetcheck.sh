#!/usr/bin/env bash
# Schwingen over loopback (#1269, src/Combat/Schwing* + SchwingNetProbe): a dedicated server on the fixture, its calendar read as
# 18 August 2027 (day 5 of the Älplerfest, the day the Schwingen opens), one server-run bot, and two clients. Everything is checked
# on the REMOTE peer too. A asks B with /schwing, B accepts; A's kick is a Gammen B sees winding up (A's copy in the throw pose) and
# braces with an Act; then B stands there until A throws it down; B lies in the sawdust, A's copy kneels over it, A mashes the tug and
# pins it; both see A's copy brush the sawdust off. A burst of 60 forged acts must be refused by the server's budget (the script
# greps its log). Off the festival nothing is paid. At the ring both press E: paired, a bout longer than the paid minimum, the
# winner and the loser are paid on their own books. A alone in the queue is given the bot; /schwing bot asks the nearest bot.
#   GODOT=<exe> [SHOTS=1] [SPEED=1] [PORT=] tools/schwingnetcheck.sh
# At /speed 4 (A's --checkspeed, #931): the bout runs on the server's scaled delta, the probe's waits are simulated seconds.
. "$(dirname "$0")/lib/twoclient.sh" schwingnet
PORT=${PORT:-7957}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-08-18 --systems network,physics,occasions,adventure --bots 1 --admin-password schwingnet)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/schwingnet_$f.log"; done
tc_server 500 120 "$OUT/schwingnet_server.log" "${SARGS[@]}"
tc_client 400 "$OUT/schwingnet_A.log" --connect 127.0.0.1:$PORT --name SchwingA $WORLD $CLIENT --schwingnet A --checkspeed ${SPEED:-4} &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 400 "$OUT/schwingnet_B.log" $WIN --connect 127.0.0.1:$PORT --name SchwingB $WORLD $CLIENT --nocapture --schwingnet B
wait $A
tc_stop
S="$OUT/schwingnet_server.log"
grep -h "\[schwingnet" "$OUT/schwingnet_A.log" "$OUT/schwingnet_B.log"
grep -h "\[schwing\]\|\[festival\].*schwingen" "$S" | head -60
refused=$(grep -c "refused .*over the budget" "$S")
echo "[schwingnetcheck] forged acts refused by the budget: $refused"
code=0
tc_ok 2 "$OUT/schwingnet_A.log" "$OUT/schwingnet_B.log" || code=1
[ "$refused" -ge 1 ] || { echo "[schwingnetcheck] the server never refused the forged burst"; code=1; }
echo "[schwingnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
