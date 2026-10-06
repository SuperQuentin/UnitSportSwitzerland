#!/usr/bin/env bash
# Waking a dormant car over loopback (#499, src/Vehicles/ParkingNetProbe): a headless dedicated
# server on the `parking` fixture course; headless client A finds a dormant car in the lot and wakes
# it; client B joins after and must see the SAME vehicle under the same deterministic name, standing
# in the same bay, with its own dormant copy gone.
#   tools/parkingnetcheck.sh         (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# The fixture course means no terrain data is needed, so this runs on a fresh clone.
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${PARKING_PORT:-7849}
COURSE=fixture:parking
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --chunks $COURSE > $OUT/parkingnet_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/parkingnet_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done

guard_run 240 $OUT/parkingnet_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --chunks $COURSE --name ParkA --parkingnet A --traffic 0 &
A=$!
# B joins only once A has woken one: what it sees must come from the server, not from its own guess
for _ in $(seq 1 200); do grep -q "is a real" $OUT/parkingnet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
guard_run 240 $OUT/parkingnet_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --chunks $COURSE --name ParkB --parkingnet B --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/parkingnet_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[parkingnet" $OUT/parkingnet_A.log $OUT/parkingnet_B.log
echo "[parkingnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
