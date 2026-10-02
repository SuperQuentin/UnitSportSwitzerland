#!/usr/bin/env bash
# Swimming over loopback (#301): a headless dedicated server on the lake fixture course and two
# clients. A swims (a crawl, treading water, a dive to 6 m, back up); B floats 7 m away and must
# see A's copy in the swim pose, the right style, and as deep under the surface as A says it is.
# No terrain data needed.
#   tools/swimnetcheck.sh             (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 [STYLE=ps1] tools/swimnetcheck.sh   (B windowed: its view of A in test_output/swimnet_B_*.png)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${SWIM_PORT:-7843}
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
"$GODOT" --headless --path . -- --server --port $PORT $WORLD > $OUT/swimnet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/swimnet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
"$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name SwimmerA $WORLD --swimnet A > $OUT/swimnet_A.log 2>&1 < /dev/null &
A=$!
guard_watch $A > /dev/null
WINDOW=--headless; [ -n "${SHOTS:-}" ] && WINDOW=
STYLEARG=; [ -n "${STYLE:-}" ] && STYLEARG="--style $STYLE"
guard_run 300 $OUT/swimnet_B.log "$GODOT" $WINDOW --path . -- --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view first --nocapture --time 14 $STYLEARG --swimnet B
for _ in $(seq 1 30); do kill -0 "$A" 2>/dev/null || break; sleep 1; done
grep -h "\[swimnet" $OUT/swimnet_A.log $OUT/swimnet_B.log
code=0
grep -q "\[swimnet A\] RESULT: ok" $OUT/swimnet_A.log || code=1
grep -q "\[swimnet B\] RESULT: ok" $OUT/swimnet_B.log || code=1
echo "[swimnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
