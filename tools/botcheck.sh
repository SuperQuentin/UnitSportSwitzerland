#!/usr/bin/env bash
# Bots the server runs, seen from two clients (src/Bots/BotProbe, #837): a dedicated server
# (--generated-world) with 3 bots round the spawn. A sees them marked, sees one walk on the server's
# collision, walks up to it and shoots it down with a pistol (the server takes the hits itself), then
# sees it stand up again (free roam knocks out); B sees the same bot, and sees it go down.
#   GODOT=<exe> tools/botcheck.sh        screenshot: test_output/bots_a_down.png (windowed runs)
. "$(dirname "$0")/lib/twoclient.sh" bots
AT=2583250,1113250
AT_B=2583262,1113250   # never on top of A
PORT=7836
tc_server 300 120 "$OUT/bots_server.log" --server --generated-world --port $PORT --no-query --bots 3 --botsat $AT --botlog
client() { tc_client 240 "$OUT/bots_$1.log" --connect 127.0.0.1:$PORT --name "Bot$1" --cache "$OUT/bots_cache_$1" \
    --at "$2" --view first --botcheck "$1"; }
client A "$AT" & A=$!
client B "$AT_B"
wait $A
tc_stop
grep -h "\[bots" "$OUT/bots_A.log" "$OUT/bots_B.log" | grep -v " say "
grep -h "\[bots\] \(spawned\|removed\)\|\[pvp\] peer" "$OUT/bots_server.log" | head -20
if tc_ok 2 "$OUT"/bots_A.log "$OUT"/bots_B.log; then
    echo "[botcheck] RESULT: ok"; exit 0
fi
echo "[botcheck] RESULT: FAILED (see $OUT/bots_*.log)"; exit 1
