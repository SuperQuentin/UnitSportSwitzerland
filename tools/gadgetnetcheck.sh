#!/usr/bin/env bash
# Gadgets over loopback (#275, src/Build/GadgetNetProbe): a headless dedicated server on a generated
# world; headless client A strings a zipline and sets a trampoline; client B joins after, must get
# both from the join snapshot (drawn), then watch A's body ride the cable, and see both go at the end.
#   tools/gadgetnetcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# WARNING: the server keeps them in the real user://placed/server.json of this project (A takes its
# own away at the end, and anything a crashed run of "GadgetA" left at the start).
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${GADGET_PORT:-7845}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --generated-world > $OUT/gadgetnet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/gadgetnet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 300 $OUT/gadgetnet_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name GadgetA --gadgetnet A --traffic 0 &
A=$!
# B joins only once A has built: what it sees comes from the join snapshot
for _ in $(seq 1 200); do grep -q "say placed" $OUT/gadgetnet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
guard_run 240 $OUT/gadgetnet_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name GadgetB --gadgetnet B --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/gadgetnet_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[gadgetnet" $OUT/gadgetnet_A.log $OUT/gadgetnet_B.log
grep -h "\[placed\]" $OUT/gadgetnet_server.log
echo "[gadgetnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
