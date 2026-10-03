#!/usr/bin/env bash
# The water over loopback (#299): a headless dedicated server on the lake fixture course started
# gamey (--sea-state gamey), and a headless client on the same course that must get the sea state
# on join, see the same wave surface as the server at the server's wave time (/water E N, to the
# millimetre, three points), and get an admin's /seastate storm. No terrain data needed.
#   tools/watercheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
PORT=${WATER_PORT:-7841}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
SERVER=
cleanup() { [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"; [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock; }
trap cleanup EXIT
# under tools/test.sh the runner already holds the lock (GUARD_LOCK_HELD): taking it again would wait forever
[ -n "${GUARD_LOCK_HELD:-}" ] || guard_lock 1800 600 || exit 1
guard_wait_ram 4 600 || exit 1

WORLD="--world fixture --chunks fixture:lake"
"$GODOT" --headless --path . -- --server --port $PORT $WORLD --sea-state gamey --admin-password test \
  > $OUT/watercheck_server.log 2>&1 < /dev/null &
SERVER=$!
guard_watch $SERVER > /dev/null
for _ in $(seq 1 120); do
  grep -q "server listening" $OUT/watercheck_server.log 2>/dev/null && break
  kill -0 "$SERVER" 2>/dev/null || break
  sleep 1
done
guard_run 240 $OUT/watercheck_client.log "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --name Swimmer $WORLD --watercheck net
code=$?
grep -h "\[watercheck\]" $OUT/watercheck_client.log
grep -h "sea state\|\[admin\]" $OUT/watercheck_server.log
grep -q "RESULT: ok" $OUT/watercheck_client.log && code=0 || code=1
echo "[watercheck net] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
