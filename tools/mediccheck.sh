#!/usr/bin/env bash
# The medic armband over loopback (#218, src/Items/PvpProbe.Medic.cs): a dedicated server with --pvp, a 600 s
# cooldown and a 3 s switch delay. Two non-medics hurt each other; A's /medic on is refused after its hit; B's delay
# is cancelled by moving and by a hit, then B wears it and A sees it; rounds and forged hits between the medic and
# A hurt nobody; a Battle Royale takes it off and gives it back; /medic off is immediate. Then A rejoins under the
# same name and is still refused.     GODOT=<exe> tools/mediccheck.sh     screenshot: test_output/pvp_medic_a_sees_b.png
. "$(dirname "$0")/lib/twoclient.sh" medic
AT=2583250,1113250
AT_B=2583256,1113250
PORT=${PORT:-7787}
tc_server 600 120 "$OUT/medic_server.log" --server --generated-world --port $PORT --pvp --medic-cooldown 600 --medic-delay 3 \
    --admin-password medic --brpace 0.08
client() { tc_client 400 "$OUT/medic_$1.log" --windowed --connect 127.0.0.1:$PORT --name "Pvp$2" --cache "$OUT/medic_cache_$2" \
    --at "$3" --view first --pvpcheck "$2" --pvpexpect "$4"; }
client A A "$AT" medic & A=$!
client B B "$AT_B" medic
wait $A
client A2 A "$AT" rejoin
tc_stop
grep -h "\[pvp\|\[medic" "$OUT/medic_A.log" "$OUT/medic_B.log" "$OUT/medic_A2.log" "$OUT/medic_server.log"
if tc_ok 3 "$OUT"/medic_A.log "$OUT"/medic_B.log "$OUT"/medic_A2.log; then
    echo "[mediccheck] RESULT: ok"; exit 0
fi
echo "[mediccheck] RESULT: FAILED (see $OUT/medic_*.log)"; exit 1
