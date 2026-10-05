#!/usr/bin/env bash
# A farm stand over loopback (#494, src/Farming/SellNetProbe): a dedicated server on the fixture's
# straight road; client A sets a stand up with Use by the road (the server measures it), stocks 30
# potatoes; client B joins after, gets the stand and its crates from the join snapshot, walks up and
# buys 3 for cash; A sees 27 left and 3 x the price in its honesty box and collects it; B sees the same
# 27 and the box empty, and is refused collecting it. The server's user://farm/stands.json must hold
# the stand while it stands. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/sellnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" sellnet
PORT=${PORT:-7884}
WORLD="--world fixture --chunks fixture:straight"
tc_server 300 120 "$OUT/sellnet_server.log" --server --port $PORT $WORLD --farmmonth 7
tc_client 260 "$OUT/sellnet_A.log" --connect 127.0.0.1:$PORT --name SellA $WORLD --traffic 0 --farmmonth 7 --sellnet A & A=$!
# B joins only once A's stand is stocked: what it sees first comes from the snapshot
for _ in $(seq 1 200); do grep -q "say stand" "$OUT/sellnet_A.log" 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
# the server saves on a worker: give it a moment
saved=0
for _ in $(seq 1 15); do
  file=$(find "$OUT/userdata_sellnet" -path "*farm/stands.json" 2>/dev/null | head -1)
  [ -n "$file" ] && grep -q '"Count":30' "$file" && { saved=1; break; }
  sleep 1
done
tc_client 220 "$OUT/sellnet_B.log" --connect 127.0.0.1:$PORT --name SellB $WORLD --traffic 0 --farmmonth 7 --sellnet B & B=$!
wait $A $B
tc_stop
grep -h "\[sellnet\|\[stand\]" "$OUT/sellnet_A.log" "$OUT/sellnet_B.log" "$OUT/sellnet_server.log"
code=0
tc_ok 2 "$OUT/sellnet_A.log" "$OUT/sellnet_B.log" || code=1
if [ $saved = 1 ]; then echo "[sellnetcheck] server file $file held the stocked stand"
else echo "[sellnetcheck] no server stands file with the 30 potatoes"; code=1; fi
if [ $code = 0 ]; then echo "[sellnetcheck] RESULT: ok"; exit 0; fi
echo "[sellnetcheck] RESULT: FAILED (see $OUT/sellnet_*.log)"; exit 1
