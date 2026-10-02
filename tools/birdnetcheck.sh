#!/usr/bin/env bash
# Shared birds over loopback (src/Birds/BirdNetProbe, #143): a dedicated server (--generated-world) and two
# headless clients. Both must be sent the same birds by the server; A shoots one through the real item path
# (killed on the server, scored in A's journal only, falling on B's screen too); A's air shot flushes the
# birds for B. Output in test_output/birdnet_*.log.  GODOT=<exe> [PORT=] [SERVER_ARGS=] [WINDOWED=1] [TOWN=1] [SWARM=n] tools/birdnetcheck.sh [E,N]
# The clients write user://birds.json (a kill scores in A's journal): test_output/userdata_birdnet (lib/twoclient.sh).
. "$(dirname "$0")/lib/twoclient.sh" birdnet
AT=${1:-2583250,1113250}
PORT=${PORT:-7795}
# SERVER_ARGS= (empty) serves the real terrain of UNITSPORT_CHUNKS instead of the generated world
tc_server 480 120 "$OUT/birdnet_server.log" --title "#143 birdnet server" --server ${SERVER_ARGS---generated-world} --port $PORT
# WINDOWED=1: the clients open windows and save pictures of each step (test_output/birdnet_A_*.png, _B_)
# TOWN=1: the town birds instead of the hunt (perches, a dropping on A seen by B, a tame bird flushed and landing)
EXTRA=; [ -n "${TOWN:-}" ] && EXTRA=--birdtown
client() { tc_client 420 "$OUT/birdnet_$1.log" --title "#143 birdnet client $1" --connect 127.0.0.1:$PORT --name "Bird$1" --cache "$OUT/birdnet_cache_$1" \
    --at "$AT" --view first --birdnetcheck "$1" $EXTRA; }
# SWARM=n: n swarm bots join as well (src/Net/Swarm.cs), for a light multiplayer check
if [ -n "${SWARM:-}" ]; then
    tc_client 420 "$OUT/birdnet_swarm.log" --title "#143 birdnet swarm" --swarm "$SWARM" --first 0 --total "$SWARM" --seed 1 \
        --connect 127.0.0.1:$PORT --cache "$OUT/birdnet_cache_swarm" --seconds 400 &
    _tc_servers+=($!)   # stopped at exit, with the server
fi
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[birdnet\|\[birds\] killed" "$OUT/birdnet_A.log" "$OUT/birdnet_B.log"
if tc_ok 2 "$OUT/birdnet_A.log" "$OUT/birdnet_B.log"; then echo "[birdnetcheck] RESULT: ok"; exit 0; fi
echo "[birdnetcheck] RESULT: FAILED (see $OUT/birdnet_*.log)"; exit 1
