#!/usr/bin/env bash
# Gun lockers and safes over loopback (#165): a dedicated server and two clients in one building with a
# locked container. A reads it with the smart binoculars at the door, B's wrong combination is refused,
# A cracks the real dial, B sees the door swing open on its own and the same contents, and still finds it
# open after leaving and coming back (src/Loot/LockSyncProbe).
#   CHUNKS=<terrain_chunks> GODOT=<exe> tools/locksynccheck.sh [epoch] [E,N]   (default: a random restock period, Riddes)
# The server writes the lock/take masks to user://loot: test_output/userdata_locksync (lib/twoclient.sh).
. "$(dirname "$0")/lib/twoclient.sh" locksync
EP=${1:-$((800000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2583250,1113250}
PORT=7793
# a full-world server blends its horizon for ~30 s before it listens
tc_server 420 120 $OUT/locksync_server.log --server --port $PORT --lootepoch "$EP" "${CH[@]}"
tc_client 300 $OUT/locksync_a.log --windowed --connect 127.0.0.1:$PORT --name LockA --cache "$OUT/locksync_cache_a" \
    --at "$AT" --lootepoch "$EP" --locksynccheck A "${CH[@]}" &
A=$!
sleep 3
tc_client 300 $OUT/locksync_b.log --windowed --connect 127.0.0.1:$PORT --name LockB --cache "$OUT/locksync_cache_b" \
    --at "$AT" --lootepoch "$EP" --locksynccheck B "${CH[@]}"
wait $A
tc_stop
grep -h "\[locksync" $OUT/locksync_a.log $OUT/locksync_b.log
grep -h "\[loot\].*\(cracked\|wrong\)" $OUT/locksync_server.log
if tc_ok 2 $OUT/locksync_a.log $OUT/locksync_b.log; then
    echo "[locksynccheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[locksynccheck] RESULT: FAILED (epoch $EP, see $OUT/locksync_*.log)"; exit 1
