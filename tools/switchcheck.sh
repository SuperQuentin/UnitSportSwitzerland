#!/usr/bin/env bash
# Car switches over loopback (#48) and car presets (#40): a dedicated server and two clients. The
# driver logs in as admin (so the server lets it park the car it conjured), gets in the NA6CE, puts
# the top down and the lights on with the real key actions, fits the Rally-raid preset, then gets
# out; the watcher (windowed: a headless client draws no parked vehicles) must see all of it on the
# moving car and on the parked one, and saves test_output/switchcheck_watch.png.
#   tools/switchcheck.sh [E,N]      (default: the spawn; CHUNKS=<dir> for another terrain_chunks)
. "$(dirname "$0")/lib/twoclient.sh" switch
AT=${1:-}
PORT=7796
EXTRA=${EXTRA:-}        # more client args, e.g. EXTRA="--time 13" for a daylight shot
ATARG=()
[ -n "$AT" ] && ATARG=(--at "$AT")
C1=$(mktemp -d); C2=$(mktemp -d)
tc_server 200 120 $OUT/switchcheck_server.log --server --port $PORT --admin-password switchcheck ${CH[@]+"${CH[@]}"}
tc_client 190 $OUT/switchcheck_driver.log --connect 127.0.0.1:$PORT --name Driver --cache "$C1" --switchcheck driver switchcheck ${ATARG[@]+"${ATARG[@]}"} ${CH[@]+"${CH[@]}"} --traffic 0 $EXTRA &
sleep 2
tc_client 185 $OUT/switchcheck_watch.log --windowed --connect 127.0.0.1:$PORT --name Watcher --cache "$C2" --switchcheck watch $OUT/switchcheck_watch.png ${ATARG[@]+"${ATARG[@]}"} ${CH[@]+"${CH[@]}"} --traffic 0 $EXTRA
code=$?
sleep 2
grep -h "\[switchcheck\]" $OUT/switchcheck_driver.log $OUT/switchcheck_watch.log
tc_stop
rm -r "$C1" "$C2"
exit $code
