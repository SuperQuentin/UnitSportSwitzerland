#!/usr/bin/env bash
# Sleepers over loopback (#644, src/Net/Sleepers + SleeperProbe): a dedicated server on the flat
# fixture. Client A steps 30 m and leaves: it must lie down asleep. Client B (its own user://, so
# another identity key) must see one sleeper. A comes back with the same user:// (same key): the
# server must wake it where it slept, and B must see the sleeper go. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/sleepercheck.sh
. "$(dirname "$0")/lib/twoclient.sh" sleepers
PORT=${PORT:-7877}
WORLD="--world fixture --chunks fixture:flat"
tc_server 400 120 "$OUT/sleepers_server.log" --server --port $PORT $WORLD
tc_client 200 "$OUT/sleepers_A1.log" --connect 127.0.0.1:$PORT --name Sleepy $WORLD --sleepers leave
# B under its own user://: a second identity
B_UD="$OUT/userdata_sleepers_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"; B_UD=$(cd "$B_UD" && pwd)
if _guard_windows; then B_ENV=(APPDATA="$(cygpath -w "$B_UD")"); else B_ENV=(XDG_DATA_HOME="$B_UD"); fi
( export "${B_ENV[@]}"; tc_client 300 "$OUT/sleepers_B.log" --connect 127.0.0.1:$PORT --name Watcher $WORLD --sleepers watch ) & B=$!
# B is in and has seen the sleeper before A comes back
for i in $(seq 1 150); do grep -q "ok   one sleeper shown" "$OUT/sleepers_B.log" 2>/dev/null && break; kill -0 $B 2>/dev/null || break; sleep 1; done
tc_client 200 "$OUT/sleepers_A2.log" --connect 127.0.0.1:$PORT --name Sleepy $WORLD --sleepers wake
wait $B
tc_stop
grep -h "\[sleepers" "$OUT/sleepers_A1.log" "$OUT/sleepers_B.log" "$OUT/sleepers_A2.log" "$OUT/sleepers_server.log"
if tc_ok 3 "$OUT/sleepers_A1.log" "$OUT/sleepers_B.log" "$OUT/sleepers_A2.log" \
   && grep -q "falls asleep" "$OUT/sleepers_server.log" && grep -q "wakes at" "$OUT/sleepers_server.log"; then
  echo "[sleepercheck] RESULT: ok"; exit 0
fi
echo "[sleepercheck] RESULT: FAILED (see $OUT/sleepers_*.log)"; exit 1
