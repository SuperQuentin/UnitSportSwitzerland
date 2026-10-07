#!/usr/bin/env bash
# Driving a car into an apartment block's underground garage, down its ramp, parking and reversing
# out, over loopback (#558 PR 2, src/Player/GarageProbe `--garagecheck drive`/`watch`).
#
# A headless dedicated server on the synthetic `fixture:garage` course (three blocks of flats on a
# street, each with a garage door: no terrain data and no hash of a key to hope for). Client A
# takes a car, drives in through the roll-up door, down the ramp into the car park, parks, gets out,
# gets back in and reverses up the ramp and out through the door. Client B watches from the street
# and then from the car park, so what the REMOTE peer sees is checked and not only the driver's:
# the door open, the car going in, and the car inside the building one storey below the door.
#
#   GODOT=<exe> [PORT=] [WINDOWED=1] [RAMP=along|square] tools/rampnetcheck.sh
#
# `--traffic 0`: a traffic car shoved the test car off a barn's door once (garage-buildings).
. "$(dirname "$0")/lib/twoclient.sh" rampnet
PORT=${PORT:-7868}
PW=pw558
# RAMP=along drives the block whose ramp runs along the facade (#694), RAMP=square the others; none: the nearest
WORLD="--world fixture --chunks fixture:garage --traffic 0 --doorkind Apartment${RAMP:+ --garageramp $RAMP}"
tc_server 400 120 "$OUT/rampnet_server.log" --server --port $PORT --world fixture --chunks fixture:garage --admin-password $PW
# B first, so it is already watching the door when A arrives at it
if [ -z "${NOWATCH:-}" ]; then
  tc_client 300 "$OUT/rampnet_B.log" --connect 127.0.0.1:$PORT --name RampB $WORLD --garagecheck watch --park-wait ${PARK_WAIT:-66} &
  B=$!
else B=; : > "$OUT/rampnet_B.log"; fi
tc_client 300 "$OUT/rampnet_A.log" --connect 127.0.0.1:$PORT --name RampA $WORLD \
  --garagecheck drive $PW --drive-m 16 --brake-m 20 --park-wait ${PARK_WAIT:-66}
tc_stop
[ -n "$B" ] && wait $B
code=0
# A's verdict is its own RESULT line
grep -a "RESULT" "$OUT/rampnet_A.log" | tail -1 | grep -q "RESULT: ok" || code=1
# B's is what it SAW: the garage door standing open, the driver inside the block, and below the
# door (a level of the basement), none of which B is told by its own guess
grep -aqE "door [0-9]+_[0-9]+_[0-9]+_[1-9][0-9]* open True" "$OUT/rampnet_B.log" \
  || { echo "[rampnetcheck] the watcher never saw the garage door open"; code=1; }
grep -aq "inside Apartment .* level -1" "$OUT/rampnet_B.log" \
  || { echo "[rampnetcheck] the watcher never saw the driver down in the basement"; code=1; }
grep -ahE "\[garage\] (drive|watch) .*(RESULT: ok|stopped after|still inside|level -1|descended)" \
  "$OUT/rampnet_A.log" "$OUT/rampnet_B.log" | tail -6
echo "[rampnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
