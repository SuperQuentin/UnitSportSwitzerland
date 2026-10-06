#!/usr/bin/env bash
# Elevators and flats' front doors over loopback (#557, src/Interiors/LiftSyncProbe): a dedicated server
# and two clients in one apartment block. Both stand in its elevator on the ground floor, A picks the top
# floor and both must arrive together, each seeing the other; A cracks a locked flat door up there with the
# dial and B must see it unlocked and open, then B shuts it and A must see it shut.
#   GODOT=<exe> tools/liftsynccheck.sh [E,N]        (generated world; CHUNKS=<terrain_chunks> for real data)
# Headless clients; WINDOWED=1 draws them and saves pictures to test_output/liftsync_{A,B}_*.png.
. "$(dirname "$0")/lib/twoclient.sh" liftsync
# a generated village with blocks of flats in its core (the probe also searches the villages nearest)
AT=${1:-2584978,1114253}
PORT=7797
GEN=(); [ -z "${CHUNKS:-}" ] && GEN=(--generated-world)
tc_server 420 120 $OUT/liftsync_server.log --server --port $PORT ${GEN[@]+"${GEN[@]}"} ${CH[@]+"${CH[@]}"}
tc_client 360 $OUT/liftsync_a.log --connect 127.0.0.1:$PORT --name LiftA --cache "$OUT/liftsync_cache_a" \
    --at "$AT" --traffic 0 --liftsynccheck A ${CH[@]+"${CH[@]}"} &
A=$!
sleep 3
tc_client 360 $OUT/liftsync_b.log --connect 127.0.0.1:$PORT --name LiftB --cache "$OUT/liftsync_cache_b" \
    --at "$AT" --traffic 0 --liftsynccheck B ${CH[@]+"${CH[@]}"}
wait $A
tc_stop
grep -h "\[liftsync\|\[lift\]" $OUT/liftsync_a.log $OUT/liftsync_b.log
grep -h "\[flatdoor\]" $OUT/liftsync_server.log
if tc_ok 2 $OUT/liftsync_a.log $OUT/liftsync_b.log; then
    echo "[liftsynccheck] RESULT: ok"; exit 0
fi
echo "[liftsynccheck] RESULT: FAILED (see $OUT/liftsync_*.log)"; exit 1
