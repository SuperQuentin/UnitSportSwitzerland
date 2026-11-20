#!/usr/bin/env bash
# A lift rider on another peer's screen (#915, src/World/Ropeways/LiftNetProbe): a dedicated server
# and two clients at the bottom station of a chairlift. A boards and rides; B must see A's copy
# exactly on its chair (placed from the shared clock, within 0.5 m) and sitting.
# By default on the fun fixture course's chairlift (#1609, FunCourse.ChairX): no terrain data, the
# probe places A and B by course metres, so it runs the same under TEST_FRAME=earth. With CHUNKS=<dir>
# of real tiles it stands at the chairlift above Verbier instead (LIFT_AT, LV95; tier 3 by hand).
#   GODOT=<exe> [CHUNKS= [LIFT_AT=E,N]] [PORT=] tools/liftnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" liftnet
PORT=${PORT:-7891}
if [ -n "${CHUNKS:-}" ]; then
  WORLD=(${CH[@]+"${CH[@]}"}); AT=(--at "${LIFT_AT:-2584726,1105180}")
else
  WORLD=(--world fixture --chunks fixture:fun --systems lifts,physics,network --traffic 0); AT=()
fi
tc_server 400 120 "$OUT/liftnet_server.log" --server --port $PORT "${WORLD[@]}"
tc_client 340 "$OUT/liftnet_A.log" "${WORLD[@]}" --connect 127.0.0.1:$PORT --name LiftA ${AT[@]+"${AT[@]}"} --liftnet A & A=$!
mkdir -p "$OUT/userdata_liftnet_B"
( tc_userdata "$OUT/userdata_liftnet_B"; tc_client 340 "$OUT/liftnet_B.log" "${WORLD[@]}" --connect 127.0.0.1:$PORT --name LiftB ${AT[@]+"${AT[@]}"} --liftnet B ) & B=$!
wait $A $B
tc_stop
grep -h "\[liftnet" "$OUT/liftnet_A.log" "$OUT/liftnet_B.log"
if tc_ok 2 "$OUT/liftnet_A.log" "$OUT/liftnet_B.log"; then echo "[liftnetcheck] RESULT: ok"; exit 0; fi
echo "[liftnetcheck] RESULT: FAILED (see $OUT/liftnet_*.log)"; exit 1
