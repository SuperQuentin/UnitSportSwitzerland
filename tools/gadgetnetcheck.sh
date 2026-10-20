#!/usr/bin/env bash
# Gadgets over loopback (#275, src/Build/GadgetNetProbe): a headless dedicated server on a generated
# world; headless client A strings a zipline and sets a trampoline; client B joins after, must get
# both from the join snapshot (drawn), then watch A's body ride the cable, and see both go at the end.
#   tools/gadgetnetcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
# The server's placed gadgets live in this run's own user:// (tools/lib/twoclient.sh), never the real one.
. "$(dirname "$0")/lib/twoclient.sh" gadgetnet
# the flat test world (#992): nothing here needs real or generated ground; REAL=1 boots the generated world as before
SW=(--world fixture); CW=(--world fixture)
[ -n "${REAL:-}" ] && { SW=(--generated-world); CW=(); }
PORT=${GADGET_PORT:-7845}

tc_server 1800 120 $OUT/gadgetnet_server.log --server --port $PORT "${SW[@]}"
tc_client 300 $OUT/gadgetnet_A.log --connect 127.0.0.1:$PORT ${CW[@]+"${CW[@]}"} --name GadgetA --gadgetnet A --traffic 0 ${ORIGINSTRESS:+--originstress $ORIGINSTRESS} &
A=$!
# B joins only once A has built: what it sees comes from the join snapshot
for _ in $(seq 1 1000); do grep -q "say placed" $OUT/gadgetnet_A.log 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 0.2; done
tc_client 240 $OUT/gadgetnet_B.log --connect 127.0.0.1:$PORT ${CW[@]+"${CW[@]}"} --name GadgetB --gadgetnet B --traffic 0 ${ORIGINSTRESS:+--originstress $ORIGINSTRESS}
wait $A
code=0
# the verdict is each client's last RESULT line
for c in A B; do grep "RESULT" $OUT/gadgetnet_$c.log | tail -1 | grep -q "RESULT: ok" || code=1; done
grep -h "\[gadgetnet" $OUT/gadgetnet_A.log $OUT/gadgetnet_B.log
grep -h "\[placed\]" $OUT/gadgetnet_server.log
echo "[gadgetnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
