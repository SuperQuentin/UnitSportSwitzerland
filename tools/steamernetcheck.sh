#!/usr/bin/env bash
# The paddle steamer over loopback (#303): a headless dedicated server on the lake fixture course,
# calm, with an admin password, and two clients (`--steamernet A|B`). A drives the steamer; B walks
# aboard over the gangway's plank (from a quay it builds alongside), climbs to the upper deck and
# rides standing while A runs FULL AHEAD through a gamey swell; both peers must agree where B
# stands on the ship. Then B goes over the rail and swims, and A must see it in the water.
#   tools/steamernetcheck.sh              (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 STYLE=ps1 tools/steamernetcheck.sh   (A windowed: its view of B on deck,
#                                         test_output/steamer/<style>/remote_passenger.png)
. "$(dirname "$0")/lib/twoclient.sh" steamernet
PORT=${STEAMER_PORT:-7846}

WORLD="--world fixture --chunks fixture:lake"
tc_server 1800 120 $OUT/steamernet_server.log --server --port $PORT $WORLD --sea-state calm --admin-password test
WINDOW=; [ -n "${SHOTS:-}" ] && WINDOW=--windowed
tc_client 540 $OUT/steamernet_A.log $WINDOW --connect 127.0.0.1:$PORT --name SkipperA $WORLD --nocapture --time 14 ${STYLE:+--style $STYLE} --steamernet A &
A=$!
tc_client 480 $OUT/steamernet_B.log --connect 127.0.0.1:$PORT --name PassengerB $WORLD --steamernet B
for _ in $(seq 1 200); do kill -0 "$A" 2>/dev/null || break; guard_nap 0.2; done
grep -h "\[steamernet" $OUT/steamernet_A.log $OUT/steamernet_B.log
code=0
grep -q "\[steamernet A\] RESULT: ok" $OUT/steamernet_A.log || code=1
grep -q "\[steamernet B\] RESULT: ok" $OUT/steamernet_B.log || code=1
echo "[steamernetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
