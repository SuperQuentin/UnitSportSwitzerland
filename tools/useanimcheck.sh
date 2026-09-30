#!/usr/bin/env bash
# Use animations over loopback (src/Items/UseAnimProbe): dedicated server + client A (first person: drinks, eats, GPS, hat)
# + client B (remote: must see A's Mouth arm pose and hat). Screenshots go to test_output/useanim_*.png.
#   GODOT=<exe> tools/useanimcheck.sh [E,N]
set -u
AT=${1:-2583250,1113250}
PORT=7794
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 200 "$GODOT" --headless --path . -- --server --generated-world --port $PORT > "$OUT/useanim_server.log" 2>&1 & SERVER=$!
sleep 12
timeout 150 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name UseA --cache "$OUT/useanim_cache_A" --at "$AT" --view first --useanim A > "$OUT/useanim_A.log" 2>&1 & A=$!
timeout 150 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name UseB --cache "$OUT/useanim_cache_B" --at "$AT" --view third --useanim B > "$OUT/useanim_B.log" 2>&1 & B=$!
wait $A; wait $B
kill $SERVER 2>/dev/null
grep -h "\[useanim" "$OUT"/useanim_A.log "$OUT"/useanim_B.log
if [ "$(grep -h "RESULT: ok" "$OUT"/useanim_A.log "$OUT"/useanim_B.log | wc -l)" -eq 2 ]; then echo "[useanim] RESULT: ok"; exit 0; fi
echo "[useanim] RESULT: FAILED (see $OUT/useanim_*.log)"; exit 1
