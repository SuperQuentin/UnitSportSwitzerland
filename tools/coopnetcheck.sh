#!/usr/bin/env bash
# A real farm co-op over loopback (#494, docs/notes/farming/produce-economy.md): a dedicated server on
# the real map draws no doors, so a delivery names the co-op's building and the server plans it
# (ShopService.CoopPlanNear -> InteriorManager.GetOrCreate). One client finds the nearest co-op among
# its loaded doors, delivers 40 sacks of wheat from a combine in its yard (paid, the server names
# that building), and is refused the co-op's key claimed from 200 m off and another building's key
# at its own door (src/Farming/CoopNetProbe). Real map data: not in checkmap.txt, run by hand.
#   CHUNKS=<terrain_chunks> GODOT=<exe> tools/coopnetcheck.sh [E,N]   (default: Seeland farmland by Aarberg)
. "$(dirname "$0")/lib/twoclient.sh" coopnet
AT=${1:-2585000,1208000}
PORT=7841
tc_server 480 120 $OUT/coopnet_server.log --server --port $PORT --admin-password test ${CH[@]+"${CH[@]}"}
tc_client 420 $OUT/coopnet_a.log --connect 127.0.0.1:$PORT --name CoopA --cache "$OUT/coopnet_cache_a" \
    --at "$AT" --traffic 0 --coopnet A ${CH[@]+"${CH[@]}"}
tc_stop
grep -h "\[coopnet A\]" $OUT/coopnet_a.log
grep -h "\[shop\] peer" $OUT/coopnet_server.log
KEY=$(grep -o "say coop [0-9_]*" $OUT/coopnet_a.log | head -1 | awk '{print $3}')
PAID=$(grep -c "delivered 40 Wheat to the farm co-op $KEY for [0-9]* CHF" $OUT/coopnet_server.log)
if [ -n "$KEY" ] && [ "$PAID" = 1 ] && tc_ok 1 $OUT/coopnet_a.log; then
    echo "[coopnetcheck] RESULT: ok (co-op $KEY near $AT)"; exit 0
fi
echo "[coopnetcheck] RESULT: FAILED (co-op '$KEY', server paid $PAID times; see $OUT/coopnet_*.log)"; exit 1
