#!/usr/bin/env bash
# The paddle steamer over loopback (#303): a headless dedicated server on the lake fixture course,
# calm, with an admin password, and two clients (`--steamernet A|B`). A drives the steamer; B walks
# aboard over the gangway's plank (from a quay it builds alongside), climbs to the upper deck and
# rides standing while A runs FULL AHEAD through a gamey swell; both peers must agree where B
# stands on the ship. Then B goes over the rail and swims, and A must see it in the water.
#   tools/steamernetcheck.sh              (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 STYLE=ps1 tools/steamernetcheck.sh   (A windowed: its view of B on deck,
#                                         test_output/steamer/<style>/remote_passenger.png)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${STEAMER_PORT:-7846}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=; A=
cleanup() { [ -n "$A" ] && _guard_kill_tree "$A"; [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 5 600 || exit 1

WORLD="--world fixture --chunks fixture:lake"
"$GODOT" --headless --path . -- --server --port $PORT $WORLD --sea-state calm --admin-password test > $OUT/steamernet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/steamernet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
WINDOW=--headless; [ -n "${SHOTS:-}" ] && WINDOW=
"$GODOT" $WINDOW --path . -- --connect 127.0.0.1:$PORT --name SkipperA $WORLD --nocapture --time 14 ${STYLE:+--style $STYLE} --steamernet A > $OUT/steamernet_A.log 2>&1 < /dev/null &
A=$!
guard_watch $A > /dev/null
guard_run 480 $OUT/steamernet_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name PassengerB $WORLD --steamernet B
for _ in $(seq 1 40); do kill -0 "$A" 2>/dev/null || break; sleep 1; done
grep -h "\[steamernet" $OUT/steamernet_A.log $OUT/steamernet_B.log
code=0
grep -q "\[steamernet A\] RESULT: ok" $OUT/steamernet_A.log || code=1
grep -q "\[steamernet B\] RESULT: ok" $OUT/steamernet_B.log || code=1
echo "[steamernetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
