#!/usr/bin/env bash
# Walking about in a bus over loopback (#162), all headless on the fixture world: a dedicated
# server, a driver (a) and a walker (b) (`--decknet a|b`), then one client alone on a fresh server
# (`--decknet solo`: up from the wheel into the parked bus, out, the door buttons, back in, sit,
# stand, the wheel). Reads the RESULT lines (a headless run may exit 139 after its result).
#   tools/decknetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" decknet
PORT=7816
PW=deckcheck
code=0
tc_server 260 120 $OUT/decknet_server.log --server --port $PORT --world fixture --admin-password $PW
sleep 2
tc_client 220 $OUT/decknet_a.log --connect 127.0.0.1:$PORT --world fixture --decknet a $PW &
DRIVER=$!
sleep 3
tc_client 220 $OUT/decknet_b.log --connect 127.0.0.1:$PORT --world fixture --decknet b
wait $DRIVER
tc_stop
grep -q "a RESULT: ok" $OUT/decknet_a.log || code=1
grep -q "b RESULT: ok" $OUT/decknet_b.log || code=1

tc_server 200 120 $OUT/decknet_solo_server.log --server --port $PORT --world fixture --admin-password $PW
sleep 2
tc_client 180 $OUT/decknet_solo.log --connect 127.0.0.1:$PORT --world fixture --decknet solo $PW
tc_stop
grep -q "solo RESULT: ok" $OUT/decknet_solo.log || code=1

grep -h "\[deck\].*\(PASS\|FAIL\|RESULT\)" $OUT/decknet_a.log $OUT/decknet_b.log $OUT/decknet_solo.log
echo "[decknetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
