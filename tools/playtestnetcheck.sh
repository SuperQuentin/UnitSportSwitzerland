#!/usr/bin/env bash
# The playtest recorder over loopback (#844, src/Playtest/Recording/RecorderNetCheck): a headless
# dedicated server on the fixture world and two headless clients, all three recording
# (--playtest-record). A walks its body round a 15 m circle for 30 s; B watches. B passes only if
# its trace and the server's both have A going round, matched by node name, B drew A within a
# few metres of where the server had it at the same wall-clock time, and the top view draws the
# server-vs-client offsets (test_output/playtest/recordnet_top.png).
#   GODOT=<exe> [PORT=] tools/playtestnetcheck.sh      output in test_output/playtestnet_*.log
. "$(dirname "$0")/lib/twoclient.sh" playtestnet
PORT=${PORT:-7894}
S=test_output/playtest/netcheck
rm -rf "$S"
tc_server 300 120 "$OUT/playtestnet_server.log" --server --port $PORT --world fixture --playtest-record $S/server
tc_client 240 "$OUT/playtestnet_A.log" --connect 127.0.0.1:$PORT --name RecA --world fixture --playtest-record $S/A --playtest-nomovie --playtest-recordnet A & A=$!
tc_client 240 "$OUT/playtestnet_B.log" --connect 127.0.0.1:$PORT --name RecB --world fixture --playtest-record $S/B --playtest-nomovie \
  --playtest-recordnet B --playtest-server-trace $S/server & B=$!
wait $A $B
tc_stop
grep -ah "\[recordnet" "$OUT/playtestnet_A.log" "$OUT/playtestnet_B.log"
if tc_ok 2 "$OUT/playtestnet_A.log" "$OUT/playtestnet_B.log"; then echo "[playtestnetcheck] RESULT: ok"; exit 0; fi
echo "[playtestnetcheck] RESULT: FAILED (see $OUT/playtestnet_*.log)"; exit 1
