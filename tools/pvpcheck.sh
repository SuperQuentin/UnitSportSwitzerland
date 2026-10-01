#!/usr/bin/env bash
# Foot weapons against a player over loopback (src/Items/PvpProbe, #178): two runs on a dedicated server
# (--generated-world). With --pvp: A shoots B with the rifle, the pistol (through B's vest) and the knife, then
# finishes B, who must name A as its killer while A sees B's body down. Without it: nothing may hurt B.
#   GODOT=<exe> tools/pvpcheck.sh        screenshots: test_output/pvp_{a_down,b_down}.png
set -u
AT=2583250,1113250
PORT=7797
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
run() {   # $1 = on|off
    local flag=""; [ "$1" = on ] && flag="--pvp"
    timeout 300 "$GODOT" --headless --path . -- --server --generated-world --port $PORT $flag > "$OUT/pvp_server_$1.log" 2>&1 & SERVER=$!
    sleep 12
    client() { timeout 220 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name "Pvp$2" --cache "$OUT/pvp_cache_$2" \
        --at "$AT" --view first --pvpcheck "$2" --pvpexpect "$1" > "$OUT/pvp_$1_$2.log" 2>&1; }
    client "$1" A & A=$!
    client "$1" B
    wait $A
    kill $SERVER 2>/dev/null
    grep -h "\[pvp" "$OUT/pvp_$1_A.log" "$OUT/pvp_$1_B.log" "$OUT/pvp_server_$1.log"
}
trap 'kill $SERVER 2>/dev/null' EXIT
run on
run off
if [ "$(grep -h "RESULT: ok" "$OUT"/pvp_on_A.log "$OUT"/pvp_on_B.log "$OUT"/pvp_off_A.log "$OUT"/pvp_off_B.log | wc -l)" -eq 4 ]; then
    echo "[pvpcheck] RESULT: ok"; exit 0
fi
echo "[pvpcheck] RESULT: FAILED (see $OUT/pvp_*.log)"; exit 1
