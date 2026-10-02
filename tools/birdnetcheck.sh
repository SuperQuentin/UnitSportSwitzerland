#!/usr/bin/env bash
# Shared birds over loopback (src/Birds/BirdNetProbe, #143): a dedicated server (--generated-world) and two
# headless clients. Both must be sent the same birds by the server; A shoots one through the real item path
# (killed on the server, scored in A's journal only, falling on B's screen too); A's air shot flushes the
# birds for B. Output in test_output/birdnet_*.log.  GODOT=<exe> [PORT=] [SERVER_ARGS=] [WINDOWED=1] [TOWN=1] [SWARM=n] tools/birdnetcheck.sh [E,N]
# WARNING: the clients write the real user://birds.json of this machine (a kill scores in A's journal).
. "$(dirname "$0")/lib/guard.sh"; guard_watch $$ > /dev/null  # RAM watchdog: kills this script's processes before Windows/WSL run out (testing note)
set -u
AT=${1:-2583250,1113250}
PORT=${PORT:-7795}
GODOT=${GODOT:-godot}
cd "$(dirname "$0")/.."
OUT=test_output
mkdir -p "$OUT"
# SERVER_ARGS= (empty) serves the real terrain of UNITSPORT_CHUNKS instead of the generated world
timeout 480 "$GODOT" --headless --path . -- --title "#143 birdnet server" --server ${SERVER_ARGS---generated-world} --port $PORT > "$OUT/birdnet_server.log" 2>&1 & SERVER=$!
sleep 12
# WINDOWED=1: the clients open windows and save pictures of each step (test_output/birdnet_A_*.png, _B_)
HEADLESS=--headless; [ -n "${WINDOWED:-}" ] && HEADLESS=
# TOWN=1: the town birds instead of the hunt (perches, a dropping on A seen by B, a tame bird flushed and landing)
EXTRA=; [ -n "${TOWN:-}" ] && EXTRA=--birdtown
client() { timeout 420 "$GODOT" $HEADLESS --path . -- --title "#143 birdnet client $1" --connect 127.0.0.1:$PORT --name "Bird$1" --cache "$OUT/birdnet_cache_$1" \
    --at "$AT" --view first --birdnetcheck "$1" $EXTRA > "$OUT/birdnet_$1.log" 2>&1; }
# SWARM=n: n swarm bots join as well (src/Net/Swarm.cs), for a light multiplayer check
SWARMPID=
if [ -n "${SWARM:-}" ]; then
    timeout 420 "$GODOT" --headless --path . -- --title "#143 birdnet swarm" --swarm "$SWARM" --first 0 --total "$SWARM" --seed 1 \
        --connect 127.0.0.1:$PORT --cache "$OUT/birdnet_cache_swarm" --seconds 400 > "$OUT/birdnet_swarm.log" 2>&1 & SWARMPID=$!
fi
client A & A=$!
client B & B=$!
wait $A $B
# a headless Godot may ignore SIGTERM (and may exit 139 after its result): kill hard, read RESULT lines
# only what this script started: the server and its timeout wrapper, by PID (never by pattern)
# (children found through ps -ef, which Linux and Git Bash on Windows both have; no pkill there)
for P in $SERVER $SWARMPID; do
    for C in $(ps -ef | awk -v p=$P '$3 == p { print $2 }'); do kill -9 $C 2>/dev/null; done; kill -9 $P 2>/dev/null
done
grep -h "\[birdnet\|\[birds\] killed" "$OUT/birdnet_A.log" "$OUT/birdnet_B.log"
if [ "$(grep -h "RESULT: ok" "$OUT/birdnet_A.log" "$OUT/birdnet_B.log" | wc -l)" -eq 2 ]; then echo "[birdnetcheck] RESULT: ok"; exit 0; fi
echo "[birdnetcheck] RESULT: FAILED (see $OUT/birdnet_*.log)"; exit 1
