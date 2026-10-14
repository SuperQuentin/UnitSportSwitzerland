#!/usr/bin/env bash
# A building site's skip searched over loopback (#627, src/Loot/YardLootNetProbe): a headless
# dedicated server on the GENERATED world, which puts a building site in every village (#607), and
# headless clients at the site. A finds the nearest site whose skip holds something this restock
# period and opens it outdoors with E; B, there already with it open, must see the stack A takes
# drop out of its panel by itself; then C joins late (B gone, A still online) and must find that
# stack gone and the rest there. The server works every container out from the tile's own files.
#   GODOT=<exe> [PORT=] [AT=E,N] [WINDOWED=1] tools/yardlootnetcheck.sh    output in test_output/yardlootnet_*.log
#   WINDOWED=1: A also saves the prompt and the open panel to test_output/yardloot_{prompt,panel}.png
. "$(dirname "$0")/lib/twoclient.sh" yardlootnet
PORT=${PORT:-7893}
# the generated Foundations site --constructioncheck lists by the spawn
AT=${AT:-2588796,1118441}
WORLD="--generated-world --traffic 0 --at $AT"
tc_server 480 120 "$OUT/yardlootnet_server.log" --server --port $PORT --generated-world
tc_client 360 "$OUT/yardlootnet_A.log" --connect 127.0.0.1:$PORT --name YardA $WORLD --yardlootnet A & A=$!
tc_client 240 "$OUT/yardlootnet_B.log" --connect 127.0.0.1:$PORT --name YardB $WORLD --yardlootnet B & B=$!
wait $B
# the late joiner: B has gone, A is still there and tells it which skip. Its own user:// (a
# player of its own: with B's identity key it would wake at B's sleeper)
rm -rf "$OUT/userdata_yardlootnet_C"; mkdir -p "$OUT/userdata_yardlootnet_C"
( tc_userdata "$OUT/userdata_yardlootnet_C"
  WINDOWED= tc_client 240 "$OUT/yardlootnet_C.log" --connect 127.0.0.1:$PORT --name YardC $WORLD --yardlootnet C ) & C=$!
wait $A $C
tc_stop
grep -ah "\[yardlootnet" "$OUT/yardlootnet_A.log" "$OUT/yardlootnet_B.log" "$OUT/yardlootnet_C.log" | grep -v " say "
grep -ah "\[loot\]" "$OUT/yardlootnet_server.log" | tail -6
if tc_ok 3 "$OUT/yardlootnet_A.log" "$OUT/yardlootnet_B.log" "$OUT/yardlootnet_C.log"; then echo "[yardlootnetcheck] RESULT: ok"; exit 0; fi
echo "[yardlootnetcheck] RESULT: FAILED (see $OUT/yardlootnet_*.log)"; exit 1
