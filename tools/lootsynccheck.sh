#!/usr/bin/env bash
# Loot over loopback: a dedicated server and two clients search the SAME container in one building.
# A takes a stack while B has it open; B's panel must drop it on its own, B taking it too must be
# refused, B takes the rest, and every stack ends up in exactly one pack (src/Loot/LootSyncProbe).
#   [CHUNKS=<terrain_chunks>] [GODOT=<exe>] tools/lootsynccheck.sh [epoch] [E,N]      (default: a random restock period, Riddes)
# The epoch must be one no earlier run emptied (the server remembers what was taken, in a fresh
# test_output/userdata_lootsync per run unless USERDATA= says otherwise), and it must roll a
# ground-floor container with two stacks; if it says there is none, run it again.
. "$(dirname "$0")/lib/twoclient.sh" lootsync
EP=${1:-$((700000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2583250,1113250}
PORT=7792
# a full-world server blends its horizon for ~30 s before it listens
tc_server 300 120 $OUT/lootsync_server.log --server --port $PORT --lootepoch "$EP" "${CH[@]}"
# windowed clients, as verified; untested headless
tc_client 240 $OUT/lootsync_a.log --windowed --connect 127.0.0.1:$PORT --name LootA --cache "$OUT/lootsync_cache_a" \
    --at "$AT" --lootepoch "$EP" --lootsynccheck A "${CH[@]}" &
A=$!
sleep 3
tc_client 240 $OUT/lootsync_b.log --windowed --connect 127.0.0.1:$PORT --name LootB --cache "$OUT/lootsync_cache_b" \
    --at "$AT" --lootepoch "$EP" --lootsynccheck B "${CH[@]}"
wait $A
tc_stop
grep -h "\[lootsync" $OUT/lootsync_a.log $OUT/lootsync_b.log
if tc_ok 2 $OUT/lootsync_a.log $OUT/lootsync_b.log; then
    echo "[lootsynccheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[lootsynccheck] RESULT: FAILED (epoch $EP, see $OUT/lootsync_*.log)"; exit 1
