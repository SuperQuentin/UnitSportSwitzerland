#!/usr/bin/env bash
# Foot weapons against a player over loopback (src/Items/PvpProbe, #178): two runs on a dedicated server
# (--generated-world). With --pvp: A shoots B with the rifle, the pistol (through B's vest) and the knife, then
# finishes B, who must name A as its killer while A sees B's body down. Without it: nothing may hurt B.
#   GODOT=<exe> tools/pvpcheck.sh        screenshots: test_output/pvp_{a_down,b_down}.png
. "$(dirname "$0")/lib/twoclient.sh" pvp
AT=2583250,1113250
AT_B=2583256,1113250   # never on top of A: two bodies spawned in one place throw each other kilometres
PORT=7797
run() {   # $1 = on|off
    local flag=""; [ "$1" = on ] && flag="--pvp"
    tc_server 300 120 "$OUT/pvp_server_$1.log" --server --generated-world --port $PORT $flag
    client() { tc_client 220 "$OUT/pvp_$1_$2.log" --windowed --connect 127.0.0.1:$PORT --name "Pvp$2" --cache "$OUT/pvp_cache_$2" \
        --at "$3" --view first --pvpcheck "$2" --pvpexpect "$1"; }
    client "$1" A "$AT" & A=$!
    client "$1" B "$AT_B"
    wait $A
    tc_stop
    grep -h "\[pvp" "$OUT/pvp_$1_A.log" "$OUT/pvp_$1_B.log" "$OUT/pvp_server_$1.log"
}
run on
run off
if tc_ok 4 "$OUT"/pvp_on_A.log "$OUT"/pvp_on_B.log "$OUT"/pvp_off_A.log "$OUT"/pvp_off_B.log; then
    echo "[pvpcheck] RESULT: ok"; exit 0
fi
echo "[pvpcheck] RESULT: FAILED (see $OUT/pvp_*.log)"; exit 1
