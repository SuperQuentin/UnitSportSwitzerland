#!/usr/bin/env bash
# The Sechseläuten over loopback (#1358, src/Occasions/Festivals/Sechselaeuten/SechselaeutenNetProbe.cs): a dedicated server on the fixture with its sky
# running, its calendar read as 19 April 2027 (the third Monday, held at the fixture start under --festival test) and the burn fixed at 8 minutes
# (--sechsedraw 8 --sechsepace 0.75: six seconds of burning, so the check can look at the fire without racing it). Everything is checked on the REMOTE peers:
# Tom is refused the pyre and the purse from 150 m; Lea and Tom take the Zunft's purse and bet (each is told the pool and its own bet, never the other's
# bucket or any time); the admin sets the clock to 17:59 and the sky crossing 18:00 lights the pyre; every message the clients got until the bang says
# minutes -1; the head bursts and both are told at once, the board names both bets, each is paid its share by closeness plus the play's pay in its own
# section, the stage blasts, and both grill a Cervelat. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/sechsenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" sechsenet
PORT=${PORT:-7977}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-04-19 --sechsedraw 8 --sechsepace 0.75 --systems network,occasions,adventure,sky --admin-password sechw)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/sechsenet_$f.log"; done
B_UD="$OUT/userdata_sechsenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/sechsenet_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/sechsenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --sechsenet a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/sechsenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --sechsenet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[sechsenet] the server was still up when the clients ended" || echo "[sechsenet] the server had stopped before the clients ended"
tc_stop
S="$OUT/sechsenet_server.log"
grep -h "\[festival\] sechselaeuten\|the Zunft's purse\|a bet on the" "$S"
grep -h "\[sechsenet" "$OUT"/sechsenet_A.log "$OUT"/sechsenet_B.log
lit=$(grep -c "the Böögg is lit" "$S")
bangs=$(grep -c "the head burst after 8.0 min" "$S")
bets=$(grep -c "bet 10 Batzen on" "$S")
if tc_ok 2 "$OUT/sechsenet_A.log" "$OUT/sechsenet_B.log" && [ "$lit" = 1 ] && [ "$bangs" = 1 ] && [ "$bets" = 2 ]; then
  echo "[sechsenetcheck] RESULT: ok"; exit 0
fi
echo "[sechsenetcheck] RESULT: FAILED (see $OUT/sechsenet_*.log; $bets bets, $lit light(s) and $bangs bang(s) on the server, want 2, 1 and 1)"; exit 1
