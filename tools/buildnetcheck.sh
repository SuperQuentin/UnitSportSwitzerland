#!/usr/bin/env bash
# Building over loopback (#274, src/Build/BuildNetProbe): a headless dedicated server on a generated
# world; headless client A builds (the hammer's real path, then walls and a floor on them); client B
# joins after, must get them from the join snapshot, be refused damaging or taking A's pieces (PvP
# off), then see A's own wall break (the floor it shared stays) and everything go at the end.
#   tools/buildnetcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# WARNING: the server keeps structures in the real user://structures/server.json of this project
# (A takes its own down at the end, so it ends as it started).
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${BUILD_PORT:-7841}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --generated-world > $OUT/buildnet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/buildnet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 300 $OUT/buildnet_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name BuilderA --buildnet A --traffic 0 ${ORIGINSTRESS:+--originstress $ORIGINSTRESS} &
A=$!
# B joins only once A has built: what it sees comes from the join snapshot
for _ in $(seq 1 200); do grep -q "say built" $OUT/buildnet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
guard_run 240 $OUT/buildnet_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name WatcherB --buildnet B --traffic 0 ${ORIGINSTRESS:+--originstress $ORIGINSTRESS}
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/buildnet_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[buildnet" $OUT/buildnet_A.log $OUT/buildnet_B.log
grep -h "\[build\]" $OUT/buildnet_server.log
echo "[buildnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
