#!/usr/bin/env bash
# Shops over loopback (#273, docs/notes/loot/shops.md): a headless dedicated server on the generated
# world (its village shops), client A that buys the last items of a slot for cash, one line on the
# card (the server must debit the account) and sells something back, and client B that walks into
# the same shop afterwards and must see that slot sold out. No terrain data needed. The runs keep
# their saves (shops, bank accounts) in test_output/shopcheck_appdata, wiped first: never the real ones.
#   tools/shopcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${SHOP_PORT:-7834}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
rm -rf "$OUT/shopcheck_appdata"
mkdir -p "$OUT/shopcheck_appdata"
if command -v cygpath > /dev/null; then export APPDATA="$(cygpath -w "$PWD/$OUT/shopcheck_appdata")"; fi
export XDG_DATA_HOME="$PWD/$OUT/shopcheck_appdata"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --generated-world --admin-password shopcheck --shopstuck > $OUT/shopcheck_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/shopcheck_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 480 $OUT/shopcheck_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name ShopA --shopnet A --traffic 0 &
CLIENT_A=$!
sleep 2
guard_run 475 $OUT/shopcheck_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name ShopB --shopnet B --traffic 0
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
