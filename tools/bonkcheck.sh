#!/usr/bin/env bash
# Thrown things hitting a player, and a radio carried on the back, over loopback (#261): a headless
# dedicated server on a generated world, a headless thrower that throws a stone then a radio at the
# victim, a headless victim that must be hurt, never below the floor, never knocked out, and see the
# thrower's radio on its back. No terrain data needed.
#   tools/bonkcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${BONK_PORT:-7833}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; guard_unlock; }
trap cleanup EXIT
guard_lock 1800 900 || exit 1
guard_wait_ram 4 600 || exit 1

"$GODOT" --headless --path . -- --server --port $PORT --generated-world > $OUT/bonkcheck_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/bonkcheck_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 200 $OUT/bonkcheck_thrower.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name Thrower --bonkcheck thrower --traffic 0 &
THROWER=$!
sleep 2
guard_run 195 $OUT/bonkcheck_victim.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name Victim --bonkcheck victim --traffic 0
wait $THROWER
code=0
grep -q "RESULT: ok" $OUT/bonkcheck_thrower.log || code=1
grep -q "RESULT: ok" $OUT/bonkcheck_victim.log || code=1
grep -h "\[bonkcheck\]" $OUT/bonkcheck_thrower.log $OUT/bonkcheck_victim.log
grep -h "\[bonk\]" $OUT/bonkcheck_server.log
echo "[bonkcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
