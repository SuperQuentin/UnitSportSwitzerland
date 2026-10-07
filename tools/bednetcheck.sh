#!/usr/bin/env bash
# Pallets set into a tipper's body over loopback (#615, src/Items/BedNetProbe): a headless dedicated
# server on the flat fixture with an admin password, and two headless clients. A (admin) parks an
# empty tipper of its own and lowers a pallet into it from a telehandler (the server has A, its
# authority, take it, replicated); B sees it, gets in, tips it out (A sees it set down), then A
# lowers another into the body B drives (the server hands it to B, A sees it in B's pose).
#   GODOT=<exe> [PORT=] tools/bednetcheck.sh      output in test_output/bednet_*.log
. "$(dirname "$0")/lib/twoclient.sh" bednet
PORT=${PORT:-7891}
WORLD="--world fixture --chunks fixture:flat --traffic 0"
tc_server 420 120 "$OUT/bednet_server.log" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/bednet_$1.log" --connect 127.0.0.1:$PORT --name "Bed$1" $WORLD --bednet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -ah "\[bednet" "$OUT/bednet_A.log" "$OUT/bednet_B.log" | grep -v " heard "
grep -ah "\[pallets\]" "$OUT/bednet_server.log" | tail -6
if tc_ok 2 "$OUT/bednet_A.log" "$OUT/bednet_B.log"; then echo "[bednetcheck] RESULT: ok"; exit 0; fi
echo "[bednetcheck] RESULT: FAILED (see $OUT/bednet_*.log)"; exit 1
