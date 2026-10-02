#!/usr/bin/env bash
# Boats over loopback (#302): a headless dedicated server on the lake fixture course, gamey, with an
# admin password, and two clients. A (admin) idles a speedboat in the swell, runs it slowly, then
# leaves it floating; B swims 30 m off and must draw A's boat (driven, then parked) on B's own copy
# of the waves at B's own time: its pitch following B's surface as A's follows A's, its keel as deep
# under B's surface as A's is under A's. No terrain data needed.
#   tools/boatnetcheck.sh             (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/boatnetcheck.sh     (B windowed: its view of A's boat in test_output/boatnet_B_*.png)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${BOAT_PORT:-7845}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=; A=
cleanup() { [ -n "$A" ] && _guard_kill_tree "$A"; [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 600 || exit 1
guard_wait_ram 5 600 || exit 1

WORLD="--world fixture --chunks fixture:lake"
"$GODOT" --headless --path . -- --server --port $PORT $WORLD --sea-state gamey --admin-password test > $OUT/boatnet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/boatnet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
"$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name BoaterA $WORLD --boatnet A > $OUT/boatnet_A.log 2>&1 < /dev/null &
A=$!
guard_watch $A > /dev/null
WINDOW=--headless; [ -n "${SHOTS:-}" ] && WINDOW=
guard_run 300 $OUT/boatnet_B.log "$GODOT" $WINDOW --path . -- --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view first --nocapture --time 14 --boatnet B
for _ in $(seq 1 30); do kill -0 "$A" 2>/dev/null || break; sleep 1; done
grep -h "\[boatnet" $OUT/boatnet_A.log $OUT/boatnet_B.log
code=0
grep -q "\[boatnet A\] RESULT: ok" $OUT/boatnet_A.log || code=1
grep -q "\[boatnet B\] RESULT: ok" $OUT/boatnet_B.log || code=1
echo "[boatnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
