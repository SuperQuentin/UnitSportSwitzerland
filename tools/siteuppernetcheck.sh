#!/usr/bin/env bash
# A site office's upper container over loopback (#827, src/Interiors/SiteUpperNetProbe): a headless
# dedicated server on the GENERATED world and three headless clients at a site whose office has its
# gallery. A walks up the outside stair (through WalkControls, a bot's seam), opens an upper door,
# walks in onto the plan's floor 1 and takes a stack; B, on the walkway from the start, sees the door
# open, its portal and A inside, then walks in and finds the stack gone; C joins only after A's
# take, finds the door open, climbs the stair, finds the stack gone and walks out onto the walkway.
#   GODOT=<exe> [PORT=] [AT=E,N] [EPOCH=N] tools/siteuppernetcheck.sh      output in test_output/siteuppernet_*.log
. "$(dirname "$0")/lib/twoclient.sh" siteuppernet
PORT=${PORT:-7893}
# a generated topped-out site whose office has its gallery (--constructioncheck prints its stair foot)
AT=${AT:-2591104,1117808}
# a restock period in which an upper room has something to take (the server remembers takes per run)
EPOCH=${EPOCH:-424242}
# the generated world wherever it runs: no real tiles, even where terrain_location.json has some (#943)
WORLD="--generated-world ${NOTERRAIN[*]} --traffic 0 --at $AT --lootepoch $EPOCH"
# C starts on A's "took" line: never on an earlier run's log
rm -f "$OUT"/siteuppernet_A.log "$OUT"/siteuppernet_B.log "$OUT"/siteuppernet_C.log
tc_server 480 120 "$OUT/siteuppernet_server.log" --server --port $PORT --generated-world "${NOTERRAIN[@]}" --lootepoch $EPOCH
tc_client 420 "$OUT/siteuppernet_A.log" --connect 127.0.0.1:$PORT --name UpperA $WORLD --siteuppernet A & A=$!
tc_client 420 "$OUT/siteuppernet_B.log" --connect 127.0.0.1:$PORT --name UpperB $WORLD --siteuppernet B & B=$!
# C joins late: once A has taken its stack
for i in $(seq 1 1800); do
  grep -q "\[siteupper A\] took" "$OUT/siteuppernet_A.log" 2>/dev/null && break
  kill -0 $A 2>/dev/null || break
  sleep 0.2
done
tc_client 360 "$OUT/siteuppernet_C.log" --connect 127.0.0.1:$PORT --name UpperC $WORLD --siteuppernet C & C=$!
wait $A $B $C
tc_stop
grep -ah "\[siteupper" "$OUT/siteuppernet_A.log" "$OUT/siteuppernet_B.log" "$OUT/siteuppernet_C.log"
if tc_ok 3 "$OUT/siteuppernet_A.log" "$OUT/siteuppernet_B.log" "$OUT/siteuppernet_C.log"; then echo "[siteuppernetcheck] RESULT: ok"; exit 0; fi
echo "[siteuppernetcheck] RESULT: FAILED (see $OUT/siteuppernet_*.log)"; exit 1
