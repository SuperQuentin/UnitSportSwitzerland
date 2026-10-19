#!/usr/bin/env bash
# A building site's office over loopback (#822, src/Interiors/SiteOfficeNetProbe): a headless
# dedicated server on the GENERATED world, which puts a building site in every village (#607), and
# three headless clients at one site. A opens the office door, walks in and takes a stack from one
# of its containers; B, waiting outside from the start, must see the door open, then walk in and
# find the stack gone; C joins only after A's take and must find the door open and the stack gone,
# then walks into a toilet cabin. A also puts on the office's hard hat and hi-vis vest: B and C see them.
#   GODOT=<exe> [PORT=] [AT=E,N] [EPOCH=N] tools/siteofficenetcheck.sh      output in test_output/siteofficenet_*.log
. "$(dirname "$0")/lib/twoclient.sh" siteofficenet
PORT=${PORT:-7891}
# a generated Shell site with two containers, an office and a mess room (--constructioncheck lists them)
AT=${AT:-2586561,1115543}
# a restock period in which the office has something to take (the server remembers takes per run)
EPOCH=${EPOCH:-424242}
WORLD="--generated-world --traffic 0 --at $AT --lootepoch $EPOCH"
# C starts on A's "took" line: never on an earlier run's log
rm -f "$OUT"/siteofficenet_A.log "$OUT"/siteofficenet_B.log "$OUT"/siteofficenet_C.log
tc_server 420 120 "$OUT/siteofficenet_server.log" --server --port $PORT --generated-world --lootepoch $EPOCH
tc_client 360 "$OUT/siteofficenet_A.log" --connect 127.0.0.1:$PORT --name OfficeA $WORLD --siteofficenet A & A=$!
tc_client 360 "$OUT/siteofficenet_B.log" --connect 127.0.0.1:$PORT --name OfficeB $WORLD --siteofficenet B & B=$!
# C joins late: once A has taken its stack
for i in $(seq 1 300); do
  grep -q "\[siteoffice A\] took" "$OUT/siteofficenet_A.log" 2>/dev/null && break
  kill -0 $A 2>/dev/null || break
  sleep 1
done
tc_client 300 "$OUT/siteofficenet_C.log" --connect 127.0.0.1:$PORT --name OfficeC $WORLD --siteofficenet C & C=$!
wait $A $B $C
tc_stop
grep -ah "\[siteoffice" "$OUT/siteofficenet_A.log" "$OUT/siteofficenet_B.log" "$OUT/siteofficenet_C.log"
if tc_ok 3 "$OUT/siteofficenet_A.log" "$OUT/siteofficenet_B.log" "$OUT/siteofficenet_C.log"; then echo "[siteofficenetcheck] RESULT: ok"; exit 0; fi
echo "[siteofficenetcheck] RESULT: FAILED (see $OUT/siteofficenet_*.log)"; exit 1
