#!/usr/bin/env bash
# Swimming over loopback (#301): a headless dedicated server on the lake fixture course and two
# clients. A swims (a crawl, treading water, a dive to 6 m, back up); B floats 7 m away and must
# see A's copy in the swim pose, the right style, and as deep under the surface as A says it is.
# No terrain data needed.
#   tools/swimnetcheck.sh             (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 [STYLE=ps1] tools/swimnetcheck.sh   (B windowed: its view of A in test_output/swimnet_B_*.png)
. "$(dirname "$0")/lib/twoclient.sh" swimnet
PORT=${SWIM_PORT:-7843}

WORLD="--world fixture --chunks fixture:lake"
tc_server 1800 120 $OUT/swimnet_server.log --server --port $PORT $WORLD
tc_client 360 $OUT/swimnet_A.log --connect 127.0.0.1:$PORT --name SwimmerA $WORLD --swimnet A &
A=$!
WINDOW=; [ -n "${SHOTS:-}" ] && WINDOW=--windowed
STYLEARG=; [ -n "${STYLE:-}" ] && STYLEARG="--style $STYLE"
tc_client 300 $OUT/swimnet_B.log $WINDOW --connect 127.0.0.1:$PORT --name WatcherB $WORLD --view first --nocapture --time 14 $STYLEARG --swimnet B
for _ in $(seq 1 30); do kill -0 "$A" 2>/dev/null || break; sleep 1; done
grep -h "\[swimnet" $OUT/swimnet_A.log $OUT/swimnet_B.log
code=0
grep -q "\[swimnet A\] RESULT: ok" $OUT/swimnet_A.log || code=1
grep -q "\[swimnet B\] RESULT: ok" $OUT/swimnet_B.log || code=1
echo "[swimnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
