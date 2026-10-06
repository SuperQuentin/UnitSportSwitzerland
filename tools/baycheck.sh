#!/usr/bin/env bash
# Driving a lorry through an industrial site's LOADING BAY, over loopback (#531, #496 phase 2).
#
# A headless dedicated server on the GENERATED world — which since #531 puts a works at the edge
# of every village, so this needs no terrain download and runs on a fresh clone. Client A finds
# the nearest Industrial door a vehicle fits through (a bay, never the office door beside it),
# drives in through its portal, parks, gets out, gets back in and reverses out. Client B watches
# the same door from the street, so the bay's leaf and the car crossing it are checked on the
# REMOTE peer and not only on the one doing the driving (the root CLAUDE.md's rule).
#
#   tools/baycheck.sh        (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#
# `--traffic 0`: a traffic car shoved the test car off a barn's door once (docs/notes/vehicles/
# garage-buildings.md), and a bay is no wider.
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${BAY_PORT:-7866}
PW=pw531
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again hangs
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --generated-world --admin-password $PW \
  > $OUT/bay_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/bay_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done

# B first, so it is already watching the door when A arrives at it
guard_run 240 $OUT/bay_B.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT \
  --generated-world --traffic 0 --name BayB --garagecheck watch --doorkind Industrial &
B=$!
guard_run 240 $OUT/bay_A.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT \
  --generated-world --traffic 0 --name BayA --garagecheck drive $PW --doorkind Industrial
A=$?
wait $B

code=0
# A's verdict is its own RESULT line
grep -a "RESULT" $OUT/bay_A.log | tail -1 | grep -q "RESULT: ok" || code=1
# B's is what it SAW from the street, which is the whole point of a second peer:
#   a door whose slot is not 0 — a loading bay, not the office door beside it — standing open,
#   and the other player inside the works, neither of which B is told by its own guess.
grep -aqE "door [0-9]+_[0-9]+_[0-9]+_[1-9][0-9]* open True" $OUT/bay_B.log \
  || { echo "[baycheck] the watcher never saw a BAY open (slot 0 is the office door)"; code=1; }
grep -aq "inside Industrial" $OUT/bay_B.log \
  || { echo "[baycheck] the watcher never saw the driver inside the works"; code=1; }
grep -ahE "\[garage\] (drive|watch) .*(RESULT: ok|stopped after|still inside|inside Industrial)" \
  $OUT/bay_A.log $OUT/bay_B.log | tail -4
echo "[baycheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
