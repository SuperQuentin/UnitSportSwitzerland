#!/usr/bin/env bash
# The flight Geneva -> Brest (#1710, G2 D0; src/Player/LongFlightCheck): an A320 on its autopilot over the globe's
# pyramid, ~870 km at 10 km, down to 1,500 m over Brest's leaves: the ground and the far view drawn all the way, no jump
# at a re-anchor. The cruise at 4x (FLIGHTWARP), ~25 min; FLIGHTKM=<km> stops early.
#   GODOT=<exe> [GLOBE=dir] [LEAVES=dir] [AT=E,N] [FLIGHTWARP=x] [FLIGHTKM=km] [WINDOWED=1] tools/longflightcheck.sh
# output: test_output/longflightcheck.log
NAME=longflightcheck
GLOBE=${GLOBE:-D:/UnitSportSwitzerland/cube_global}
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/brest_cube}   # the destination's leaves: drawn under it at the end
AT=${AT:-2498000,1120500}   # over Geneva airport
NEED=("$GLOBE" "$LEAVES")
TIMEOUT_S=${TIMEOUT_S:-3600}
ARGS=(--longflightcheck --globe "$GLOBE" --leaves "$LEAVES" --at "$AT" --airliner sim --flightwarp "${FLIGHTWARP:-4}" --traffic 0)
[ -n "${FLIGHTKM:-}" ] && ARGS+=(--flightkm "$FLIGHTKM")
. "$(dirname "$0")/lib/barprobe.sh"
