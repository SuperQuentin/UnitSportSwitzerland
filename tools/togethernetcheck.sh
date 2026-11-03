#!/usr/bin/env bash
# Playing together over loopback (#1266, src/Adventure/Together + TogetherNetProbe): a dedicated server on the flat
# fixture with --together-test and --fondueseed 2, Lea (lead) and Tom (follow, its own user://). Nobody counts as together
# before the party, both after it; Lea holds Tom's hand, Tom sees her body pose the hold and follows with no input; a
# fondue at a campfire (the pot state, the lost bread and the forfeit's line reach the other peer, both end Fed); a
# seeded veteran guides a newcomer: each gets a cosmetic; in a Battle Royale lobby the link is gone.
#   GODOT=<exe> [PORT=] tools/togethernetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" togethernet
PORT=${PORT:-7933}
WORLD="--world fixture --chunks fixture:flat"
SARGS=(--server --port $PORT $WORLD --systems network,adventure,sky --together-test --fondueseed 2 --admin-password togetherpw)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/togethernet_A.log"; : > "$OUT/togethernet_B.log"
tc_server 600 120 "$OUT/togethernet_server.log" "${SARGS[@]}"
tc_client 540 "$OUT/togethernet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --togethernet lead &
A=$!
B_UD="$OUT/userdata_togethernet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 540 "$OUT/togethernet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --togethernet follow ) &
B=$!
wait $A $B
tc_stop
grep -h "\[together\]\|\[fondue\]\|\[togethernet" "$OUT/togethernet_server.log" "$OUT/togethernet_A.log" "$OUT/togethernet_B.log" | grep -v " ok " | head -80
if tc_ok 2 "$OUT/togethernet_A.log" "$OUT/togethernet_B.log" && grep -q "server link" "$OUT/togethernet_server.log"; then
  echo "[togethernetcheck] RESULT: ok"; exit 0
fi
echo "[togethernetcheck] RESULT: FAILED (see $OUT/togethernet_*.log)"; exit 1
