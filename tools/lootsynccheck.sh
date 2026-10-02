#!/usr/bin/env bash
# Loot over loopback: a dedicated server and two clients search the SAME container in one building.
# A takes a stack while B has it open; B's panel must drop it on its own, B taking it too must be
# refused, B takes the rest, and every stack ends up in exactly one pack (src/Loot/LootSyncProbe).
#   [CHUNKS=<terrain_chunks>] [GODOT=<exe>] tools/lootsynccheck.sh [epoch] [E,N]      (default: a random restock period, Riddes)
# The epoch must be one no earlier run emptied (the server remembers what was taken), and it must
# roll a ground-floor container with two stacks; if it says there is none, run it again.
# WARNING: the server writes the taken masks to the real user://loot of this project.
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
EP=${1:-$((700000 + RANDOM * 8 + RANDOM % 8))}
AT=${2:-2583250,1113250}
PORT=7792
GODOT=${GODOT:-godot}
# the server and the Godot under its timeout wrapper, by PID (on Git Bash a kill stops only the wrapper)
stop() { for C in $(ps -ef | awk -v p=$1 '$3 == p { print $2 }'); do kill -9 $C 2>/dev/null; done; kill -9 $1 2>/dev/null; }
CH=()
[ -n "${CHUNKS:-}" ] && CH=(--chunks "$CHUNKS")   # a worktree has no terrain_chunks of its own
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
timeout 300 "$GODOT" --headless --path . -- --server --port $PORT --lootepoch "$EP" "${CH[@]}" > $OUT/lootsync_server.log 2>&1 &
SERVER=$!
# a full-world server blends its horizon for ~30 s before it listens
for _ in $(seq 1 120); do /usr/bin/grep -q "server listening" $OUT/lootsync_server.log 2>/dev/null && break; sleep 1; done
# windowed clients, as verified; untested headless
timeout 240 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name LootA --cache "$OUT/lootsync_cache_a" \
    --at "$AT" --lootepoch "$EP" --lootsynccheck A "${CH[@]}" > $OUT/lootsync_a.log 2>&1 &
A=$!
sleep 3
timeout 240 "$GODOT" --path . -- --connect 127.0.0.1:$PORT --name LootB --cache "$OUT/lootsync_cache_b" \
    --at "$AT" --lootepoch "$EP" --lootsynccheck B "${CH[@]}" > $OUT/lootsync_b.log 2>&1
wait $A
stop $SERVER
grep -h "\[lootsync" $OUT/lootsync_a.log $OUT/lootsync_b.log
if [ "$(grep -h "RESULT: ok" $OUT/lootsync_a.log $OUT/lootsync_b.log | wc -l)" -eq 2 ]; then
    echo "[lootsynccheck] RESULT: ok (epoch $EP)"; exit 0
fi
echo "[lootsynccheck] RESULT: FAILED (epoch $EP, see $OUT/lootsync_*.log)"; exit 1
