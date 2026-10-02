#!/usr/bin/env bash
# The flag ghost, plant and pull-up over loopback (src/Items/PlantProbe): dedicated server (--generated-world),
# client A holds the flag (ghost valid / red, plants, pulls up: test_output/plant_a_*.png), client B screenshots
# the body of A and the spawned flag in frames after the "go" of A (test_output/plant_b_NN.png).
#   GODOT=<exe> tools/plantcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" plant
AT=2583250,1113250
PORT=7796
tc_server 300 120 "$OUT/plant_server.log" --server --generated-world --traffic 0 --port $PORT
client() { tc_client 200 "$OUT/plant_$1.log" --windowed --connect 127.0.0.1:$PORT --name "Plant$1" --cache "$OUT/plant_cache_$1" \
    --at "$AT" --view first --traffic 0 --plantcheck "$1" "${@:2}"; }
client A --hold SwissFlag & A=$!
client B
wait $A
tc_stop
grep -h "\[plantcheck" "$OUT"/plant_A.log "$OUT"/plant_B.log
if tc_ok 2 "$OUT"/plant_A.log "$OUT"/plant_B.log; then
    echo "[plantcheck] RESULT: ok"; exit 0
fi
echo "[plantcheck] RESULT: FAILED (see $OUT/plant_*.log)"; exit 1
