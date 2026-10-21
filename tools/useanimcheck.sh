#!/usr/bin/env bash
# Use animations over loopback (src/Items/UseAnimProbe): dedicated server + client A (first person: drinks, eats, GPS, hat)
# + client B (remote: must see A's Mouth arm pose and hat). Screenshots go to test_output/useanim_*.png.
#   GODOT=<exe> tools/useanimcheck.sh [E,N]
. "$(dirname "$0")/lib/twoclient.sh" useanim
# the flat test world (#992): nothing here needs real or generated ground; REAL=1 boots the generated world as before
SW=(--world fixture); CW=(--world fixture)
[ -n "${REAL:-}" ] && { SW=(--generated-world); CW=(); }
AT=${1:-2583250,1113250}
PORT=7794
tc_server 200 120 "$OUT/useanim_server.log" --server "${SW[@]}" --port $PORT ${CH[@]+"${CH[@]}"}
tc_client 150 "$OUT/useanim_A.log" --windowed --connect 127.0.0.1:$PORT ${CW[@]+"${CW[@]}"} --name UseA --cache "$OUT/useanim_cache_A" --at "$AT" --view first --useanim A ${CH[@]+"${CH[@]}"} & A=$!
tc_client 150 "$OUT/useanim_B.log" --windowed --connect 127.0.0.1:$PORT ${CW[@]+"${CW[@]}"} --name UseB --cache "$OUT/useanim_cache_B" --at "$AT" --view third --useanim B ${CH[@]+"${CH[@]}"} & B=$!
wait $A; wait $B
tc_stop
grep -h "\[useanim" "$OUT"/useanim_A.log "$OUT"/useanim_B.log
if tc_ok 2 "$OUT"/useanim_A.log "$OUT"/useanim_B.log; then echo "[useanim] RESULT: ok"; exit 0; fi
echo "[useanim] RESULT: FAILED (see $OUT/useanim_*.log)"; exit 1
