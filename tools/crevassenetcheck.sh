#!/usr/bin/env bash
# The hidden crevasse over loopback (#1372, src/Adventure/Together/CrevasseNetProbe): a dedicated server on the flat fixture,
# Lea (lead, a pretend glacier) and Tom (follow, its own user://). Lea, unroped, steps on a hidden strip and falls 10 m: Tom
# sees her copy in the crevasse pose under the ground, then standing again once she climbed out. Roped to Tom, the next fall is held
# 3 m under the lip; Tom hauls with the held-E path and Lea is out. The server's stamina watch skipped her body and flagged no climb.
#   GODOT=<exe> [PORT=] tools/crevassenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" crevassenet
PORT=${PORT:-7935}
WORLD="--world fixture --chunks fixture:flat"
SARGS=(--server --port $PORT $WORLD --systems network,adventure,sky --admin-password crevassepw)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/crevassenet_A.log"; : > "$OUT/crevassenet_B.log"
tc_server 600 120 "$OUT/crevassenet_server.log" "${SARGS[@]}"
A_UD="$OUT/userdata_crevassenet_A"; rm -rf "$A_UD"; mkdir -p "$A_UD"
( tc_userdata "$A_UD"; tc_client 540 "$OUT/crevassenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --crevassenet lead ) &
A=$!
B_UD="$OUT/userdata_crevassenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 540 "$OUT/crevassenet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --crevassenet follow ) &
B=$!
wait $A $B
tc_stop
grep -h "\[together\] crevasse\|\[stamina\]\|\[crevassenet" "$OUT/crevassenet_server.log" "$OUT/crevassenet_A.log" "$OUT/crevassenet_B.log" | grep -v " ok " | head -60
if tc_ok 2 "$OUT/crevassenet_A.log" "$OUT/crevassenet_B.log" \
   && grep -q "is down a crevasse: not watched" "$OUT/crevassenet_server.log" \
   && ! grep -q "implausible climb" "$OUT/crevassenet_server.log"; then
  echo "[crevassenetcheck] RESULT: ok"; exit 0
fi
echo "[crevassenetcheck] RESULT: FAILED (see $OUT/crevassenet_*.log)"; exit 1
