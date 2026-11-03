#!/usr/bin/env bash
# The closing fireworks show over loopback (#1269 part 2, src/Occasions/Festivals/FestivalNetProbe.cs, role "show"): a dedicated server on the
# fixture with its sky running, its calendar read as 14 March 2027 (the last day of the check-only festival, --festival test). Everything is
# checked on the REMOTE peer: in the morning no show; the admin sets the clock to 21:24 and the server fixes the evening's show when the sky
# crosses 21:30; its start and seed arrive in the client's running list (no new RPC), the client plans the same show and plays its first
# shell with its sounds. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/fireworksnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" fireworksnet
PORT=${PORT:-7961}
WORLD="--world fixture --chunks fixture:flat --festival test"
SARGS=(--server --port $PORT $WORLD --date 2027-03-14 --systems network,occasions,adventure,sky --admin-password fwpw)
CLIENT="--systems network,physics,ui,occasions,adventure"
for f in A server; do : > "$OUT/fireworksnet_$f.log"; done
tc_server 300 120 "$OUT/fireworksnet_server.log" "${SARGS[@]}"
tc_client 200 "$OUT/fireworksnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --festivalnet show
tc_stop
grep -h "\[festival\]\|\[fireworks\]" "$OUT/fireworksnet_server.log"
grep -h "\[festivalnet\|\[fireworks\]" "$OUT/fireworksnet_A.log"
if tc_ok 1 "$OUT/fireworksnet_A.log" && grep -q "closing show #1 starts" "$OUT/fireworksnet_server.log"; then
  echo "[fireworksnetcheck] RESULT: ok"; exit 0
fi
echo "[fireworksnetcheck] RESULT: FAILED (see test_output/fireworksnet_*.log)"; exit 1
