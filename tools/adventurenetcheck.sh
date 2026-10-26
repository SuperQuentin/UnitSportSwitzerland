#!/usr/bin/env bash
# The Adventure layer over loopback (#1223, src/Adventure/AdventureService + AdventureNetProbe): a dedicated
# server on the flat fixture keeps every player's progress. Client A proves its key, starts the probe errand
# and walks to its first area: the server moves it on and A's mirror shows it (remote authority). A leaves,
# the server restarts, A comes back with the same user:// (same key): the errand is where it was; A hands an
# apple over and gets the rewards; in a Battle Royale lobby a quest start is refused. Client B, its own
# user:// but A's name, gets fresh progress. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/adventurenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" adventurenet
PORT=${PORT:-7927}
WORLD="--world fixture --chunks fixture:flat --adventure-test"
SARGS=(--server --port $PORT $WORLD --systems network,adventure --admin-password adventurepw)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/adventurenet_A1.log"; : > "$OUT/adventurenet_A2.log"; : > "$OUT/adventurenet_B.log"
tc_server 300 120 "$OUT/adventurenet_server1.log" "${SARGS[@]}"
tc_client 200 "$OUT/adventurenet_A1.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --adventurenet start
tc_stop
echo "[adventurenetcheck] server stopped; restarting it on the same user://"
tc_server 400 120 "$OUT/adventurenet_server2.log" "${SARGS[@]}"
tc_client 300 "$OUT/adventurenet_A2.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --adventurenet back
# B under its own user://: another identity, the same name
B_UD="$OUT/userdata_adventurenet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 200 "$OUT/adventurenet_B.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --adventurenet fresh )
tc_stop
grep -h "\[adventure" "$OUT/adventurenet_server1.log" "$OUT/adventurenet_server2.log" "$OUT/adventurenet_A1.log" \
  "$OUT/adventurenet_A2.log" "$OUT/adventurenet_B.log"
saved=$(find "$OUT/userdata_adventurenet" -path "*adventure/players/*.json" 2>/dev/null | head -1)
echo "[adventurenetcheck] saved progress: ${saved:-none}"
if tc_ok 3 "$OUT/adventurenet_A1.log" "$OUT/adventurenet_A2.log" "$OUT/adventurenet_B.log" && [ -n "$saved" ] \
   && grep -q "completes probe-errand" "$OUT/adventurenet_server2.log"; then
  echo "[adventurenetcheck] RESULT: ok"; exit 0
fi
echo "[adventurenetcheck] RESULT: FAILED (see $OUT/adventurenet_*.log)"; exit 1
