#!/usr/bin/env bash
# Shops over loopback (#273, docs/notes/loot/shops.md): a headless dedicated server on the generated
# world (its village shops), client A that buys the last items of a slot for cash, one line on the
# card (the server must debit the account) and sells something back, and client B that walks into
# the same shop afterwards and must see that slot sold out. No terrain data needed. The runs keep
# their saves (shops, bank accounts) in test_output/userdata_shopcheck, wiped first: never the real ones.
#   tools/shopcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" shopcheck
PORT=${SHOP_PORT:-7834}

tc_server 1800 120 $OUT/shopcheck_server.log --server --port $PORT --generated-world --admin-password shopcheck --shopstuck
tc_client 480 $OUT/shopcheck_A.log --connect 127.0.0.1:$PORT --name ShopA --shopnet A --traffic 0 &
CLIENT_A=$!
sleep 2
tc_client 475 $OUT/shopcheck_B.log --connect 127.0.0.1:$PORT --name ShopB --shopnet B --traffic 0
wait $CLIENT_A
code=0
for r in A B; do
  last=$(grep -a "RESULT" $OUT/shopcheck_$r.log | tail -1)
  [[ -n $last && $last != *FAIL* ]] || code=1
done
grep -ah "\[shopnet" $OUT/shopcheck_A.log $OUT/shopcheck_B.log
grep -ah "\[shop\]\|\[bank\]" $OUT/shopcheck_server.log
echo "[shopcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
