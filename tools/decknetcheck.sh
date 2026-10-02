#!/usr/bin/env bash
# Walking about in a bus over loopback (#162), all headless on the fixture world: a dedicated
# server, a driver (a) and a walker (b) (`--decknet a|b`), then one client alone on a fresh server
# (`--decknet solo`: up from the wheel into the parked bus, out, the door buttons, back in, sit,
# stand, the wheel). Reads the RESULT lines (a headless run may exit 139 after its result).
#   tools/decknetcheck.sh
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog (testing note)
set -u
GODOT=${GODOT:-godot}
PORT=7816
PW=deckcheck
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"

server() {   # $1: log
  timeout "$2" "$GODOT" --headless --path . -- --server --port $PORT --world fixture --admin-password $PW > "$1" 2>&1 &
  SERVER=$!
  for i in $(seq 1 120); do
    grep -q "server listening" "$1" 2>/dev/null && break
    sleep 1
  done
  sleep 2
}

code=0
server $OUT/decknet_server.log 260
timeout 220 "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --world fixture --decknet a $PW > $OUT/decknet_a.log 2>&1 &
DRIVER=$!
sleep 3
timeout 220 "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --world fixture --decknet b > $OUT/decknet_b.log 2>&1
wait $DRIVER
kill $SERVER 2>/dev/null
grep -q "a RESULT: ok" $OUT/decknet_a.log || code=1
grep -q "b RESULT: ok" $OUT/decknet_b.log || code=1

server $OUT/decknet_solo_server.log 200
timeout 180 "$GODOT" --headless --path . -- --connect 127.0.0.1:$PORT --world fixture --decknet solo $PW > $OUT/decknet_solo.log 2>&1
kill $SERVER 2>/dev/null
grep -q "solo RESULT: ok" $OUT/decknet_solo.log || code=1

grep -h "\[deck\].*\(PASS\|FAIL\|RESULT\)" $OUT/decknet_a.log $OUT/decknet_b.log $OUT/decknet_solo.log
echo "[decknetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
