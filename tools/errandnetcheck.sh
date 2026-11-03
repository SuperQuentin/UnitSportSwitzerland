#!/usr/bin/env bash
# The errands over loopback (#1267, src/Adventure/Errands + ErrandNetProbe): a dedicated server on the flat fixture with the
# fixture POIs and givers, Lea (lead) and Tom (follow, its own user://). Both compute the same cards for the cell; Lea takes the
# mail run at the post office (the server checks where her body stands) and shares it, Tom joins it from where he stands; both
# hand the sacks over at the three stops and the second to finish pays both the friend bonus; Tom takes two more cards and is
# refused a fourth.
#   GODOT=<exe> [PORT=] tools/errandnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" errandnet
PORT=${PORT:-7937}
WORLD="--world fixture --chunks fixture:flat --pois fixture --givers fixture"
SARGS=(--server --port $PORT $WORLD --systems network,adventure,sky)
CLIENT="--systems network,physics,ui,adventure"
: > "$OUT/errandnet_A.log"; : > "$OUT/errandnet_B.log"
tc_server 600 120 "$OUT/errandnet_server.log" "${SARGS[@]}"
tc_client 540 "$OUT/errandnet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --errandnet lead &
A=$!
B_UD="$OUT/userdata_errandnet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 540 "$OUT/errandnet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --errandnet follow ) &
B=$!
wait $A $B
tc_stop
grep -h "\[errands\]\|\[errandnet" "$OUT/errandnet_server.log" "$OUT/errandnet_A.log" "$OUT/errandnet_B.log" | grep -v " ok " | head -80
if tc_ok 2 "$OUT/errandnet_A.log" "$OUT/errandnet_B.log" && grep -q "\[errands\] bonus" "$OUT/errandnet_server.log"; then
  echo "[errandnetcheck] RESULT: ok"; exit 0
fi
echo "[errandnetcheck] RESULT: FAILED (see $OUT/errandnet_*.log)"; exit 1
