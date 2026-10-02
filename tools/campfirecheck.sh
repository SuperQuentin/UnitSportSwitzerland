#!/usr/bin/env bash
# A campfire over loopback (#272, src/Crafting/CampfireNetProbe): a headless dedicated server on the
# fixture world; headless client A crafts a campfire and lays it through the item's real path, then
# holds a torch; client B joins after, must get the fire from the join snapshot, burning (its light
# is there), see A's torch light, be refused putting out A's fire, then see it go when A puts it out.
#   tools/campfirecheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# WARNING: the server keeps placed objects in the real user://placed/server.json of this project
# (A puts its fire out at the end, so it ends as it started).
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${CAMPFIRE_PORT:-7843}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --world fixture > $OUT/campfire_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/campfire_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 300 $OUT/campfire_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name FireA --campfirenet A --world fixture --traffic 0 &
A=$!
# B joins only once A's fire is lit: what it sees comes from the join snapshot
for _ in $(seq 1 200); do grep -q "say lit" $OUT/campfire_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
guard_run 240 $OUT/campfire_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name FireB --campfirenet B --world fixture --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/campfire_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[campfirenet" $OUT/campfire_A.log $OUT/campfire_B.log
grep -h "\[placed\]" $OUT/campfire_server.log
echo "[campfirecheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
