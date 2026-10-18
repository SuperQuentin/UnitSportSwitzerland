#!/usr/bin/env bash
# UFO sightings over loopback (#863): a headless dedicated server on the flat fixture (a pasture with
# a herd south-west of the spawn) runs the ufo occasion and plans an abduction, then a landing. Both
# clients must get the same plans, see the same cow lifted by the beam and set down again; A boards
# the landed saucer (a parked UFO, E at its ramp) and B sees A flying it, the sighting's saucer taken.
#   tools/sightingnetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/sightingnetcheck.sh    (B windowed: its view in test_output/sightingnet_B_*.png)
. "$(dirname "$0")/lib/twoclient.sh" sightingnet
PORT=${SIGHTING_PORT:-7863}
WORLD="--world fixture"
tc_server 420 120 $OUT/sightingnet_server.log --server --port $PORT $WORLD --occasion ufo --sighting abduction,landing
tc_client 380 $OUT/sightingnet_A.log --connect 127.0.0.1:$PORT --name SpotterA $WORLD --sightingnet A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN="--windowed --windowed-shots --time 22 --style cartoon"
( tc_userdata "$OUT/userdata_sightingnet_B"; tc_client 380 $OUT/sightingnet_B.log $WIN --connect 127.0.0.1:$PORT --name SpotterB $WORLD --view third --nocapture --sightingnet B )
wait $A
tc_stop
grep -h "\[sightingnet\|\[sightings\]\|\[herds\]" $OUT/sightingnet_server.log $OUT/sightingnet_A.log $OUT/sightingnet_B.log
code=0
tc_ok 2 $OUT/sightingnet_A.log $OUT/sightingnet_B.log || code=1
# the same plans and the same cow on both clients
for what in "plan Abduction" "plan Landing" "cow"; do
  a=$(grep -h "\[sightingnet A\] $what" $OUT/sightingnet_A.log | sed 's/.*\] //' | head -1)
  b=$(grep -h "\[sightingnet B\] $what" $OUT/sightingnet_B.log | sed 's/.*\] //' | head -1)
  if [ -z "$a" ] || [ "$a" != "$b" ]; then echo "[sightingnetcheck] FAIL $what differs: A '$a' B '$b'"; code=1
  else echo "[sightingnetcheck] ok   both: $a"; fi
done
echo "[sightingnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
