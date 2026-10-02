#!/usr/bin/env bash
# The shotgun's feel over loopback (src/Items/ShotgunProbe): dedicated server (--generated-world), client A holds the
# shotgun aimed and fires (screenshots of its own view), client B stands nearby and screenshots A's body:
#   test_output/gunshot_{a_aimed,a_kick,a_pump,b_kick,b_pump,b_after}.png
#   GODOT=<exe> tools/gunshotcheck.sh [--gunside]   (side: B looks along A's shoulder line; files gunshot_s_*.png)
. "$(dirname "$0")/lib/twoclient.sh" gunshot
AT=2583250,1113250
PORT=7795
tc_server 300 120 "$OUT/gunshot_server.log" --server --generated-world --port $PORT
client() { tc_client 200 "$OUT/gunshot_$1.log" --windowed --connect 127.0.0.1:$PORT --name "Gun$1" --cache "$OUT/gunshot_cache_$1" \
    --at "$AT" --view first --gunshot "$1" "${@:2}"; }
client A --hold Shotgun --aim & A=$!
client B "$@"
wait $A
tc_stop
grep -h "\[gunshot" "$OUT"/gunshot_A.log "$OUT"/gunshot_B.log
if tc_ok 2 "$OUT"/gunshot_A.log "$OUT"/gunshot_B.log; then
    echo "[gunshotcheck] RESULT: ok"; exit 0
fi
echo "[gunshotcheck] RESULT: FAILED (see $OUT/gunshot_*.log)"; exit 1
