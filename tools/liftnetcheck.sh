#!/usr/bin/env bash
# A lift rider on another peer's screen (#915, src/World/Ropeways/LiftNetProbe): a dedicated server
# and two clients at the bottom station of a chairlift above Verbier. A boards and rides; B must
# see A's copy exactly on its chair (placed from the shared clock, within 0.5 m) and sitting.
# Needs the real terrain with the road tiles (CHUNKS=<dir> from a worktree).
#   GODOT=<exe> [CHUNKS=] [PORT=] tools/liftnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" liftnet
PORT=${PORT:-7891}
AT="--at 2584726,1105180"
tc_server 400 120 "$OUT/liftnet_server.log" --server --port $PORT ${CH[@]+"${CH[@]}"}
tc_client 340 "$OUT/liftnet_A.log" ${CH[@]+"${CH[@]}"} --connect 127.0.0.1:$PORT --name LiftA $AT --liftnet A & A=$!
mkdir -p "$OUT/userdata_liftnet_B"
( tc_userdata "$OUT/userdata_liftnet_B"; tc_client 340 "$OUT/liftnet_B.log" ${CH[@]+"${CH[@]}"} --connect 127.0.0.1:$PORT --name LiftB $AT --liftnet B ) & B=$!
wait $A $B
tc_stop
grep -h "\[liftnet" "$OUT/liftnet_A.log" "$OUT/liftnet_B.log"
if tc_ok 2 "$OUT/liftnet_A.log" "$OUT/liftnet_B.log"; then echo "[liftnetcheck] RESULT: ok"; exit 0; fi
echo "[liftnetcheck] RESULT: FAILED (see $OUT/liftnet_*.log)"; exit 1
