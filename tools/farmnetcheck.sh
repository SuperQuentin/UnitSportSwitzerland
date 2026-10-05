#!/usr/bin/env bash
# Farm fields over loopback (#494, src/Farming/FarmNetProbe): a dedicated server on the flat fixture in
# July (--farmmonth 7: the wheat is ripe); client A ploughs a strip with a machine stroke and tills a
# cell by hand; client B joins after and must get the strip from the subscribe snapshot, then sees
# A's second strip live, and is refused ploughing the potatoes from 200 m away. The server's
# user://farm file must hold the cells. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/farmnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" farmnet
PORT=${PORT:-7881}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/farmnet_server.log" --server --port $PORT $WORLD --farmmonth 7
tc_client 260 "$OUT/farmnet_A.log" --connect 127.0.0.1:$PORT --name FarmA $WORLD --traffic 0 --farmnet A & A=$!
# B joins only once A's strip is answered: what it sees first comes from the snapshot
for _ in $(seq 1 200); do grep -q "say worked" "$OUT/farmnet_A.log" 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
tc_client 220 "$OUT/farmnet_B.log" --connect 127.0.0.1:$PORT --name FarmB $WORLD --traffic 0 --farmnet B & B=$!
wait $A $B
tc_stop
grep -h "\[farmnet\|\[farm\]" "$OUT/farmnet_A.log" "$OUT/farmnet_B.log" "$OUT/farmnet_server.log"
code=0
tc_ok 2 "$OUT/farmnet_A.log" "$OUT/farmnet_B.log" || code=1
# the server saved what A did, under its own user://farm
file=$(find "$OUT/userdata_farmnet" -path "*farm/*.json" 2>/dev/null | head -1)
if [ -n "$file" ] && grep -q '"Cells":\[[0-9]' "$file"; then echo "[farmnetcheck] server file $file holds cells"
else echo "[farmnetcheck] no server farm file with cells"; code=1; fi
if [ $code = 0 ]; then echo "[farmnetcheck] RESULT: ok"; exit 0; fi
echo "[farmnetcheck] RESULT: FAILED (see $OUT/farmnet_*.log)"; exit 1
