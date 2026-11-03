#!/usr/bin/env bash
# Montreux Jazz over loopback (#1394, src/Occasions/Festivals/MontreuxJazz/JazzNetProbe.cs): a dedicated server on the fixture, its calendar read as 10 July 2027
# (day 9 of 16, held at the fixture start under --festival test), its sky read as 21:30 (--festivalhour) and its songs a few seconds long (--jazzbpm 240, on the
# server and both clients). Everything is checked on the REMOTE peers: Lea and Tom form a party, stand at the stage-front rail and mark themselves (the Jazz pass
# arrives in each pack, a mark from 150 m is refused), hear the same two songs and are each paid the group yield and the same points in their own sections, the
# party board holds both; at the call for the bis Lea claps beats 0-5 and Tom beats 2-7 on the shared clock: the meter fills to 12 on both screens, the band returns
# and each is paid the yield on the bis. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/montreuxnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" montreuxnet
PORT=${PORT:-7976}
WORLD="--world fixture --chunks fixture:flat --festival test"
TUNE="--jazzbpm 240 --festivalhour 21.5"
SARGS=(--server --port $PORT $WORLD $TUNE --date 2027-07-10 --systems network,occasions,adventure)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A B server; do : > "$OUT/montreuxnet_$f.log"; done
B_UD="$OUT/userdata_montreuxnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
tc_server 600 120 "$OUT/montreuxnet_server.log" "${SARGS[@]}"
tc_client 500 "$OUT/montreuxnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $TUNE $CLIENT --montreuxnet a &
A=$!
( tc_userdata "$B_UD"; tc_client 500 "$OUT/montreuxnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $TUNE $CLIENT --montreuxnet b ) &
B=$!
wait $A $B
kill -0 "$SERVER" 2>/dev/null && echo "[montreuxnet] the server was still up when the clients ended" || echo "[montreuxnet] the server had stopped before the clients ended"
tc_stop
S="$OUT/montreuxnet_server.log"
grep -h "listened to a song\|the bis" "$S"
grep -h "\[montreuxnet" "$OUT"/montreuxnet_A.log "$OUT"/montreuxnet_B.log
songs=$(grep -c "listened to a song" "$S")
bis=$(grep -c "the bis" "$S")
if tc_ok 2 "$OUT/montreuxnet_A.log" "$OUT/montreuxnet_B.log" && [ "$songs" -ge 4 ] && [ "$bis" -ge 2 ]; then
  echo "[montreuxnetcheck] RESULT: ok"; exit 0
fi
echo "[montreuxnetcheck] RESULT: FAILED (see $OUT/montreuxnet_*.log; $songs songs and $bis bis paid on the server, want at least 4 and 2)"; exit 1
