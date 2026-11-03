#!/usr/bin/env bash
# Alcohol over loopback (#1271, src/Adventure/Alcohol + AlcoholNetProbe): a dedicated server on the flat fixture with
# --alcohol-test, Lea (drink) and Tom (test, its own user://). Lea drinks (back-dated, through /alcohol test): her synced
# log gives her a level and her own breathalyzer reading is the server's number; Tom holds out his device, Lea accepts,
# and both screens read the same number; Tom's own level, log and screen stay empty (Lea's level never reaches him); a beer
# from Lea's pack makes the next test say WAIT (mouth alcohol). Then a server with --alcohol off and one client: the mode
# arrives with the rules, a drink logs nothing and the device reads 0.00.
#   GODOT=<exe> [PORT=] tools/alcoholnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" alcoholnet
PORT=${PORT:-7934}
WORLD="--world fixture --chunks fixture:flat"
CLIENT="--systems network,physics,ui,adventure"
RESULT=ok

# round 1: real alcohol, two clients
SARGS=(--server --port $PORT $WORLD --systems network,adventure,sky --alcohol-test)
: > "$OUT/alcoholnet_A.log"; : > "$OUT/alcoholnet_B.log"
tc_server 600 120 "$OUT/alcoholnet_server.log" "${SARGS[@]}"
tc_client 540 "$OUT/alcoholnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --alcoholnet drink &
A=$!
B_UD="$OUT/userdata_alcoholnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 540 "$OUT/alcoholnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --alcoholnet test ) &
B=$!
wait $A $B
tc_stop
grep -h "\[alcohol\]\|\[alcoholnet" "$OUT/alcoholnet_server.log" "$OUT/alcoholnet_A.log" "$OUT/alcoholnet_B.log" | grep -v " ok " | head -60
tc_ok 2 "$OUT/alcoholnet_A.log" "$OUT/alcoholnet_B.log" && grep -q "tests Lea" "$OUT/alcoholnet_server.log" || RESULT=FAILED

# round 2: --alcohol off, one client
: > "$OUT/alcoholnet_C.log"
tc_server 600 120 "$OUT/alcoholnet_server_off.log" --server --port $PORT $WORLD --systems network,adventure,sky --alcohol off
C_UD="$OUT/userdata_alcoholnet_C"; rm -rf "$C_UD"; mkdir -p "$C_UD"
( tc_userdata "$C_UD"; tc_client 300 "$OUT/alcoholnet_C.log" --connect 127.0.0.1:$PORT --name Zoe $WORLD $CLIENT --alcoholnet off )
tc_stop
grep -h "\[alcoholnet" "$OUT/alcoholnet_C.log" | grep -v " ok " | head -20
tc_ok 1 "$OUT/alcoholnet_C.log" || RESULT=FAILED

if [ "$RESULT" = ok ]; then echo "[alcoholnetcheck] RESULT: ok"; exit 0; fi
echo "[alcoholnetcheck] RESULT: FAILED (see $OUT/alcoholnet_*.log)"; exit 1
