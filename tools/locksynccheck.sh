#!/usr/bin/env bash
# Gun lockers and safes over loopback (#165): a dedicated server and two clients in one building with a
# locked container. A reads it with the smart binoculars at the door, B's wrong combination is refused,
# A cracks the real dial, B sees the door swing open on its own and the same contents, and still finds it
# open after leaving and coming back (src/Loot/LockSyncProbe).
#   CHUNKS=<terrain_chunks> GODOT=<exe> tools/locksynccheck.sh [epoch] [E,N]   (default: a random restock period, Riddes)
# WARNING: the server writes the lock/take masks to the real user://loot of this project (a fresh epoch keeps them apart).
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
EP=${1:-$((800000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2583250,1113250}
PORT=7793
GODOT=${GODOT:-godot}
# the server and the Godot under its timeout wrapper, by PID (on Git Bash a kill stops only the wrapper)
stop() { for C in $(ps -ef | awk -v p=$1 '$3 == p { print $2 }'); do kill -9 $C 2>/dev/null; done; kill -9 $1 2>/dev/null; }
CH=()
[ -n "${CHUNKS:-}" ] && CH=(--chunks "$CHUNKS")
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 420 "$GODOT" --headless --path . -- --server --port $PORT --lootepoch "$EP" "${CH[@]}" > $OUT/locksync_server.log 2>&1 &
SERVER=$!
# a full-world server blends its horizon for ~30 s before it listens
for _ in $(seq 1 120); do /usr/bin/grep -q "server listening" $OUT/locksync_server.log 2>/dev/null && break; sleep 1; done
timeout 300 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name LockA --cache "$OUT/locksync_cache_a" \
    --at "$AT" --lootepoch "$EP" --locksynccheck A "${CH[@]}" > $OUT/locksync_a.log 2>&1 &
A=$!
sleep 3
timeout 300 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name LockB --cache "$OUT/locksync_cache_b" \
    --at "$AT" --lootepoch "$EP" --locksynccheck B "${CH[@]}" > $OUT/locksync_b.log 2>&1
wait $A
stop $SERVER
/usr/bin/grep -h "\[locksync" $OUT/locksync_a.log $OUT/locksync_b.log
/usr/bin/grep -h "\[loot\].*\(cracked\|wrong\)" $OUT/locksync_server.log
if [ "$(/usr/bin/grep -h "RESULT: ok" $OUT/locksync_a.log $OUT/locksync_b.log | wc -l)" -eq 2 ]; then
    echo "[locksynccheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[locksynccheck] RESULT: FAILED (epoch $EP, see $OUT/locksync_*.log)"; exit 1
