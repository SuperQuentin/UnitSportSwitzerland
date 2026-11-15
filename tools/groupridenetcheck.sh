#!/usr/bin/env bash
# Group rides on the home trainer over loopback (src/Trainer/GroupRides, GroupRideNetProbe, #1467): a dedicated server
# on the hairpin fixture and two headless clients on RideLink's simulated rig. A hosts a group ride on the fixture
# road's first 600 m; B finds it in the list and joins (the course comes from the server); both ride it, each checking
# the standings, the other's course metres and the other's bike on its own peer; both finish and the group closes.
# Output in test_output/groupride_*.log.
# No terrain data needed.
#   GODOT=<exe> [PORT=] [WINDOWED=1 GROUPRIDE_ARGS="--date 2027-07-15 --time 12"] tools/groupridenetcheck.sh (windowed: test_output/training/groupride_A.png, _B.png)
. "$(dirname "$0")/lib/twoclient.sh" groupride
PORT=${PORT:-7989}
WORLD="--world fixture --chunks fixture:hairpin --traffic 0"
tc_server 420 120 "$OUT/groupride_server.log" --title "#1467 groupride server" --server --port $PORT $WORLD
client() { tc_client 420 "$OUT/groupride_$1.log" --title "#1467 groupride client $1" --connect 127.0.0.1:$PORT --name "Group$1" \
    $WORLD --trainer-sim ${GROUPRIDE_ARGS:-} --groupridecheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[groupride\|\[group\]" "$OUT/groupride_A.log" "$OUT/groupride_B.log" "$OUT/groupride_server.log"
if tc_ok 2 "$OUT/groupride_A.log" "$OUT/groupride_B.log"; then echo "[groupridenetcheck] RESULT: ok"; exit 0; fi
echo "[groupridenetcheck] RESULT: FAILED (see $OUT/groupride_*.log)"; exit 1
