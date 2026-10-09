#!/usr/bin/env bash
# The same drive as tools/rampnetcheck.sh, into the block whose ramp runs ALONG the facade (#694): in off the
# door, a 90 degree turn onto the ramp, down it to the car park hall and parked; then out, back up the ramp and
# round the turn to the door. Steered (pure pursuit on the plan's frame, src/Player/GarageProbe).
#   GODOT=<exe> [PORT=] [WINDOWED=1] tools/rampalongnetcheck.sh
export RAMP=along
export PORT=${PORT:-7869}
exec "$(dirname "$0")/rampnetcheck.sh"
