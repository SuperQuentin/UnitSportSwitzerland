#!/usr/bin/env bash
# The cars in an underground car park as real vehicles, over loopback (#558 PR 3,
# src/Interiors/HallCarProbe `--hallcarcheck A|B`).
#
# A headless dedicated server on the synthetic `fixture:garage` course (three 80 x 18 m blocks of
# flats on a street, each with a garage door and a car park with cars in its bays). Client A walks in
# through the garage door and stands beside a bay car at one end of the car park; it aims at it,
# which wakes it through the server (the slot is worked out from the server's own plan and only for
# a peer inside that building), gets in, and drives it from the ramp's foot up the ramp and out through
# the door, gets out in the street and goes away. Client B stands at the far end of the car park and
# passes only on what REACHED it: every bay's car drawn and solid at first; the sleeper of A's bay
# gone (never drawn under the real car) once the vehicle exists; A's car seen climbing the ramp and
# leaving the building; and, after the respawn wait (`--dormant-respawn 12`, nobody within 40 m of
# the bay), a car in that bay again.
#
#   GODOT=<exe> [PORT=] [WINDOWED=1] tools/hallcarnetcheck.sh
#
# Windowed (WINDOWED=1) it also saves test_output/hallcar_{A,B}_*.png.
. "$(dirname "$0")/lib/twoclient.sh" hallcarnet
PORT=${PORT:-7871}
WORLD="--world fixture --chunks fixture:garage --traffic 0"
tc_server 420 120 "$OUT/hallcarnet_server.log" --server --port $PORT --world fixture --chunks fixture:garage --dormant-respawn 12
# B first, so it is already waiting when A walks in
tc_client 360 "$OUT/hallcarnet_B.log" --connect 127.0.0.1:$PORT --name HallB --cache "$OUT/hallcarnet_cache_b" $WORLD --hallcarcheck B &
B=$!
sleep 3
tc_client 360 "$OUT/hallcarnet_A.log" --connect 127.0.0.1:$PORT --name HallA --cache "$OUT/hallcarnet_cache_a" $WORLD --hallcarcheck A
wait $B
tc_stop
grep -ah "\[hallcar" "$OUT/hallcarnet_A.log" "$OUT/hallcarnet_B.log" | grep -aE "ok  |FAIL|RESULT|screenshot|block|A's car" | tail -40
if tc_ok 2 "$OUT/hallcarnet_A.log" "$OUT/hallcarnet_B.log"; then
  echo "[hallcarnetcheck] RESULT: ok"; exit 0
fi
echo "[hallcarnetcheck] RESULT: FAILED (see $OUT/hallcarnet_*.log)"; exit 1
