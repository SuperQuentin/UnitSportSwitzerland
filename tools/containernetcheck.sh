#!/usr/bin/env bash
# Object container streaming over loopback (#689, src/World/ContainerNetProbe): a headless server on
# the `parking` fixture with its containers in test_output, clients A and B, then the server killed
# (the crash path, not a clean stop) and started again on the same containers for client C.
#   A wakes two dormant cars, parks the first 8 m on (client-owned), drops an item, throws a radio,
#   waits for the go; B sees them, has none of them from 5 km away (entity interest), and has them
#   back at the lot; A goes 5 km away (the server files everything; the untouched second car turns
#   back into its bay's scenery), comes back (woken: same oid, same place);
#   C, after the restart, finds the car, the item, the radio, the first bay awake, the second asleep.
#   tools/containernetcheck.sh         (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" containernet
PORT=${CONTAINER_PORT:-7853}
COURSE=fixture:parking
DIR="$(pwd)/$OUT/containernet_store"
GO="$(pwd)/$OUT/containernet_go"
rm -rf "$DIR" "$GO"
# Godot on Windows wants Windows paths
win() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
SRV=(--server --port $PORT --chunks $COURSE --containers-dir "$(win "$DIR")" --containers-quick)
CLI=(--connect 127.0.0.1:$PORT --chunks $COURSE --traffic 0)

tc_server 300 90 $OUT/containernet_server1.log "${SRV[@]}" || { echo "[containernetcheck] RESULT: FAILED (server 1 did not start)"; exit 1; }
tc_client 240 $OUT/containernet_A.log "${CLI[@]}" --name ContA --containernet A --containernet-go "$(win "$GO")" &
A=$!
for _ in $(seq 1 150); do grep -q "ready: waiting for the go" $OUT/containernet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
tc_client 180 $OUT/containernet_B.log "${CLI[@]}" --name ContB --containernet B
touch "$GO"
wait $A
cp $OUT/containernet_server1.log $OUT/containernet_server1.done.log 2>/dev/null
tc_stop

# what A left, for C: "PARKED car <oid> <E> <N> <Alt> item <oid> <E> <N> <Alt> radio <oid> <E> <N> <Alt>"
P=$(grep -h "PARKED car" $OUT/containernet_A.log | tail -1 | sed 's/.*PARKED car //')
EXPECT=$(echo "$P" | awk '{print $1","$2","$3","$4","$6","$7","$8","$9","$11","$12","$13","$14}')
tc_server 300 90 $OUT/containernet_server2.log "${SRV[@]}" || { echo "[containernetcheck] RESULT: FAILED (server 2 did not start)"; exit 1; }
tc_client 180 $OUT/containernet_C.log "${CLI[@]}" --name ContC --containernet C --containernet-expect "$EXPECT"
tc_stop

code=0
tc_ok 3 $OUT/containernet_A.log $OUT/containernet_B.log $OUT/containernet_C.log || code=1
grep -q "put to sleep" $OUT/containernet_server1.log || { echo "[containernetcheck] the server never filed anything"; code=1; }
grep -q "woke:" $OUT/containernet_server1.log || { echo "[containernetcheck] the server never woke a container"; code=1; }
grep -q "is back in its bay" $OUT/containernet_server1.log || { echo "[containernetcheck] the untouched car never went back to its bay"; code=1; }
grep -q "woke:" $OUT/containernet_server2.log || { echo "[containernetcheck] the restarted server never woke a container"; code=1; }
grep -h "\[containernet\|\[containers\]\|back in its bay" $OUT/containernet_server1.log $OUT/containernet_A.log $OUT/containernet_B.log $OUT/containernet_server2.log $OUT/containernet_C.log
echo "[containernetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
