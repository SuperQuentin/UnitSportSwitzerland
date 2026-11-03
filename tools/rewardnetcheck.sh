#!/usr/bin/env bash
# Adventure rewards over loopback (#1227, src/Adventure/Rewards/RewardService + RewardNetProbe): a dedicated server on the
# flat fixture with the fixture POIs and the hand-placed reward spots checks every claim against its own placement. A opens
# the Common chest (the server's roll; gone for A only), cannot open it again, sends two forged chest ids (refused), picks
# an edelweiss and runs the chapel's trial; B, on at the same time with its own user://, still sees the chest closed after A
# opened it and opens it too (its own roll), still sees the edelweiss after A picked it and picks it, and is sent the
# server's trial board holding A's run; B picks four more and offers five at the chapel (its max stamina rises a step); in a
# Battle Royale lobby A's chest and an edelweiss are refused; A's travel flag becomes its portable travel point (the synced
# TravelBook.Flag), a second is refused, taken up it is cleared.
#   GODOT=<exe> [PORT=] tools/rewardnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" rewardnet
PORT=${PORT:-7933}
WORLD="--world fixture --chunks fixture:flat --pois fixture --rewardspots fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure --admin-password rewardpw)
CLIENT="--systems network,physics,ui,loot,adventure"
: > "$OUT/rewardnet_A.log"; : > "$OUT/rewardnet_B.log"
tc_server 300 120 "$OUT/rewardnet_server.log" "${SARGS[@]}"
tc_client 300 "$OUT/rewardnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --rewardnet open &
A=$!
B_UD="$OUT/userdata_rewardnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 300 "$OUT/rewardnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --rewardnet watch ) &
B=$!
wait $A $B
tc_stop
grep -h "\[rewards\|\[rewardnet" "$OUT/rewardnet_server.log" "$OUT/rewardnet_A.log" "$OUT/rewardnet_B.log"
opened=$(grep -c "opened ch:" "$OUT/rewardnet_server.log" || true)
if tc_ok 2 "$OUT/rewardnet_A.log" "$OUT/rewardnet_B.log" && [ "$opened" -ge 2 ] \
   && grep -q "No such chest" "$OUT/rewardnet_server.log" && grep -q "No adventuring now" "$OUT/rewardnet_server.log"; then
  echo "[rewardnetcheck] RESULT: ok"; exit 0
fi
echo "[rewardnetcheck] RESULT: FAILED (see $OUT/rewardnet_*.log)"; exit 1
