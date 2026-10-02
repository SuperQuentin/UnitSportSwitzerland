#!/usr/bin/env bash
# Banks over loopback (#213): a dedicated server and two clients at the bank nearest the spot. A sees the
# sign, is refused a deposit in the street, deposits and withdraws at the teller desk; in the vault B's
# wrong Simon sequence is refused, A cracks a safe through the dial and the Simon panel, B sees it open
# with the same contents; A then stands in a house cellar's shelter and music room (src/Loot/BankProbe).
#   CHUNKS=<terrain_chunks> GODOT=<exe> tools/bankcheck.sh [epoch] [E,N]   (default: a random restock period, Riddes)
# The server writes user://loot and user://bank/accounts.json (accounts BankA/BankB) in
# test_output/userdata_bank (lib/twoclient.sh).
. "$(dirname "$0")/lib/twoclient.sh" bank
EP=${1:-$((800000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2582700,1113300}
PORT=7794
tc_server 480 120 $OUT/bank_server.log --server --port $PORT --lootepoch "$EP" "${CH[@]}"
tc_client 400 $OUT/bank_a.log --windowed --connect 127.0.0.1:$PORT --name BankA --cache "$OUT/bank_cache_a" \
    --at "$AT" --view first --lootepoch "$EP" --bankcheck A "${CH[@]}" &
A=$!
sleep 3
tc_client 400 $OUT/bank_b.log --windowed --connect 127.0.0.1:$PORT --name BankB --cache "$OUT/bank_cache_b" \
    --at "$AT" --lootepoch "$EP" --bankcheck B "${CH[@]}"
wait $A
tc_stop
grep -h "\[bank [AB]\]" $OUT/bank_a.log $OUT/bank_b.log
grep -h "\[bank\]\|\[loot\].*\(cracked\|wrong\)" $OUT/bank_server.log
if tc_ok 2 $OUT/bank_a.log $OUT/bank_b.log; then
    echo "[bankcheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[bankcheck] RESULT: FAILED (epoch $EP, see $OUT/bank_*.log)"; exit 1
