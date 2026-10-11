#!/usr/bin/env bash
# Waking a dormant car over loopback (#499, src/Vehicles/ParkingNetProbe): a headless dedicated
# server on the `parking` fixture course; headless client A finds a dormant car in the lot and wakes
# it; client B joins after and must see the SAME vehicle under the same deterministic name, standing
# in the same bay, with its own dormant copy gone.
#   tools/parkingnetcheck.sh         (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" parkingnet
PORT=${PARKING_PORT:-7849}
COURSE=fixture:parking

tc_server 1800 120 $OUT/parkingnet_server.log --server --port $PORT --chunks $COURSE

tc_client 240 $OUT/parkingnet_A.log --connect 127.0.0.1:$PORT --chunks $COURSE --name ParkA --parkingnet A --traffic 0 &
A=$!
# B joins only once A has woken one: what it sees must come from the server, not from its own guess
for _ in $(seq 1 200); do grep -q "is a real" $OUT/parkingnet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
tc_client 240 $OUT/parkingnet_B.log --connect 127.0.0.1:$PORT --chunks $COURSE --name ParkB --parkingnet B --traffic 0
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/parkingnet_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[parkingnet" $OUT/parkingnet_A.log $OUT/parkingnet_B.log
echo "[parkingnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
