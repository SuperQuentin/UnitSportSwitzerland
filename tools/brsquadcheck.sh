#!/usr/bin/env bash
# A bot as a squad mate (src/BattleRoyale/BrBotProbe --brsquadcheck, #837 phase 4): a duos match of A and one bot,
# alone on its side, on a generated world at pace 4 (a long looting phase, past the mate's minutes of flight to A: the recall is a looting-phase errand, the
# stops being spread over the first circle). The mate
# jumps after A and lands near, regroups, goes to A's ping, revives A when A is down, and takes A's dogtag to a
# Postauto stop when A is out: A is recalled.
# At /speed 4: the probe's waits are simulated seconds, so the check keeps its proportions in a quarter of the
# time (the runner's per-check limit cut it at 766 s at normal speed).
#   GODOT=<exe> tools/brsquadcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" brsquad
AT=2642075,1129346   # a generated village of 173 buildings, 59 km from the spawn: the server's fill reaches it (#937)
# the seed pins the zone: a first circle that misses the village's streets has no Postauto stop for the recall
PORT=${PORT:-7837}
# the generated world wherever it runs: no real tiles, even where terrain_location.json has some (#943)
tc_server 900 120 "$OUT/brsquad_server.log" --server --generated-world "${NOTERRAIN[@]}" --port $PORT --no-query --admin-password brbots --brpace 4 --brseed ${BRSEED:-399749595} --botlog
tc_client 800 "$OUT/brsquad_A.log" --connect 127.0.0.1:$PORT --name "BRSA" --cache "$OUT/brsquad_cache_A" --at "$AT" --view first --brsquadcheck --checkspeed 4 "${NOTERRAIN[@]}"
tc_stop
grep -h "\[brbots" "$OUT/brsquad_A.log" | grep -v " say "
grep -h "^\[br\] \(revived\|recalled\|go\|recall stops\)\|revived a mate\|used a dogtag\|pinged an enemy\|took 1 Dogtag" "$OUT/brsquad_server.log" | head -20
if tc_ok 1 "$OUT"/brsquad_A.log; then
    echo "[brsquadcheck] RESULT: ok"; exit 0
fi
echo "[brsquadcheck] RESULT: FAILED (see $OUT/brsquad_*.log)"; exit 1
