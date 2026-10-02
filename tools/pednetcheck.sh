#!/usr/bin/env bash
# Pedestrians over loopback (src/World/PedNetProbe, #217): a dedicated server on real terrain (towns need
# buildings) and two headless clients in a town. People come into view without pop-in, B draws the same as A,
# turning away and back finds the same ids moved on, one behind A is solid and knocked over on the server.
#   GODOT=<exe> CHUNKS=<terrain_chunks dir> [PORT=] tools/pednetcheck.sh [E,N]   (default: Sion old town)
. "$(dirname "$0")/lib/twoclient.sh" pednet
AT=${1:-2593900,1120250}
PORT=${PORT:-7798}
[ ${#CH[@]} -gt 0 ] || { echo "[pednetcheck] needs CHUNKS=<terrain_chunks dir> (a town's buildings)"; exit 2; }
tc_server 420 180 "$OUT/pednet_server.log" --title "#217 pednet server" --server "${CH[@]}" --port $PORT
client() { tc_client 400 "$OUT/pednet_$1.log" --title "#217 pednet client $1" --connect 127.0.0.1:$PORT --name "Ped$1" \
    "${CH[@]}" --cache "$OUT/pednet_cache_$1" --at "$AT" --view first --pednetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[pednet\|\[peds\]" "$OUT/pednet_A.log" "$OUT/pednet_B.log" "$OUT/pednet_server.log"
if tc_ok 2 "$OUT/pednet_A.log" "$OUT/pednet_B.log"; then echo "[pednetcheck] RESULT: ok"; exit 0; fi
echo "[pednetcheck] RESULT: FAILED (see $OUT/pednet_*.log)"; exit 1
