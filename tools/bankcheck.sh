#!/usr/bin/env bash
# Banks over loopback (#213): a dedicated server and two clients at the bank nearest the spot. A sees the
# sign, is refused a deposit in the street, deposits and withdraws at the teller desk; in the vault B's
# wrong Simon sequence is refused, A cracks a safe through the dial and the Simon panel, B sees it open
# with the same contents; A then stands in a house cellar's shelter and music room (src/Loot/BankProbe).
#   CHUNKS=<terrain_chunks> GODOT=<exe> tools/bankcheck.sh [epoch] [E,N]   (default: a random restock period, Riddes)
# WARNING: the server writes to the real user://loot and user://bank/accounts.json of this project
# (a fresh epoch keeps the loot apart; the accounts are BankA/BankB).
set -u
EP=${1:-$((800000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2582700,1113300}
PORT=7794
GODOT=${GODOT:-godot}
CH=()
[ -n "${CHUNKS:-}" ] && CH=(--chunks "$CHUNKS")
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 480 "$GODOT" --headless --path . -- --server --port $PORT --lootepoch "$EP" "${CH[@]}" > $OUT/bank_server.log 2>&1 &
SERVER=$!
for _ in $(seq 1 120); do /usr/bin/grep -q "server listening" $OUT/bank_server.log 2>/dev/null && break; sleep 1; done
timeout 400 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name BankA --cache "$OUT/bank_cache_a" \
    --at "$AT" --view first --lootepoch "$EP" --bankcheck A "${CH[@]}" > $OUT/bank_a.log 2>&1 &
A=$!
sleep 3
timeout 400 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name BankB --cache "$OUT/bank_cache_b" \
    --at "$AT" --lootepoch "$EP" --bankcheck B "${CH[@]}" > $OUT/bank_b.log 2>&1
wait $A
kill $SERVER 2>/dev/null
/usr/bin/grep -h "\[bank [AB]\]" $OUT/bank_a.log $OUT/bank_b.log
/usr/bin/grep -h "\[bank\]\|\[loot\].*\(cracked\|wrong\)" $OUT/bank_server.log
if [ "$(/usr/bin/grep -h "RESULT: ok" $OUT/bank_a.log $OUT/bank_b.log | wc -l)" -eq 2 ]; then
    echo "[bankcheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[bankcheck] RESULT: FAILED (epoch $EP, see $OUT/bank_*.log)"; exit 1
