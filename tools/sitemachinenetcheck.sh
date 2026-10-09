#!/usr/bin/env bash
# A building site's machine woken over loopback (#616, src/Vehicles/ParkingNetProbe --parkingkind site):
# a headless dedicated server on the GENERATED world, which puts a building site in every village
# (#607), so this needs no terrain download. Client A spawns at a site digging its foundations, wakes
# its first machine by name (the excavator or the wheel loader) and waits for the real one; client B
# joins after and must see the same machine, of the same kind, at its place, with its own dormant copy
# gone.
#   GODOT=<exe> [PORT=] [AT=E,N] tools/sitemachinenetcheck.sh      output in test_output/sitemachinenet_*.log
. "$(dirname "$0")/lib/twoclient.sh" sitemachinenet
PORT=${PORT:-7886}
# the generated Foundations site nearest the default spawn (--constructioncheck lists them)
AT=${AT:-2585292,1114432}
WORLD="--generated-world --traffic 0 --at $AT --parkingkind site"
tc_server 420 120 "$OUT/sitemachinenet_server.log" --server --port $PORT --generated-world
tc_client 240 "$OUT/sitemachinenet_A.log" --connect 127.0.0.1:$PORT --name SiteA $WORLD --parkingnet A & A=$!
# B joins only once A has woken one: what it sees must come from the server, not from its own guess
for _ in $(seq 1 200); do grep -q "is a real" "$OUT/sitemachinenet_A.log" 2>/dev/null && break; kill -0 $A 2>/dev/null || break; sleep 1; done
tc_client 240 "$OUT/sitemachinenet_B.log" --connect 127.0.0.1:$PORT --name SiteB $WORLD --parkingnet B
wait $A
tc_stop
grep -ah "\[parkingnet" "$OUT/sitemachinenet_A.log" "$OUT/sitemachinenet_B.log"
if tc_ok 2 "$OUT/sitemachinenet_A.log" "$OUT/sitemachinenet_B.log"; then echo "[sitemachinenetcheck] RESULT: ok"; exit 0; fi
echo "[sitemachinenetcheck] RESULT: FAILED (see $OUT/sitemachinenet_*.log)"; exit 1
