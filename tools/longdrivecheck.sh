#!/usr/bin/env bash
# The long drive (#1710, G2 D0; #1744; src/Player/LongDriveCheck): a car on its autopilot from Geneva to Nyon on the D:
# region (~25 km of road), on one route planned on the lane graph of the corridor (the leaves' roads under earth, the
# LV95 tiles' under flat), at each road's speed, across many re-anchors: no stall, no fall, no jump. Runs in TEST_FRAME's
# frame (earth when unset): the bar asks it of both. Real time (the leaves stream on threads), ~35 min.
#   GODOT=<exe> [FRAME=flat|earth] [LEAVES=dir] [CHUNKS=dir] [AT=E,N] [TO=E,N] [CAR=n] [WINDOWED=1] tools/longdrivecheck.sh
# output: test_output/longdrivecheck.log
NAME=longdrivecheck
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks}
AT=${AT:-2500900,1121300}   # Geneva, Chambésy on the Route de Lausanne (the old town and Sécheron open on corners and closed streets the drive cannot take)
TO=${TO:-2507500,1138500}   # Nyon, the Geneva patch's north end along the lake
# a Major road the car cannot drive on (ground collision across it at Place des Nations, on flat and earth alike): round it
AVOID=${AVOID-2500145,1119932}
NEED=("$LEAVES")
FRAME=${FRAME:-${TEST_FRAME:-earth}}
TIMEOUT_S=${TIMEOUT_S:-3600}
ARGS=(--longdrivecheck --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT" --to "$TO" --car "${CAR:-4}" --traffic 0 --systems "${SYSTEMS:-terrain,physics,sky}")
[ -n "$AVOID" ] && ARGS+=(--avoid "$AVOID")
# shellcheck disable=SC2206
[ -n "${EXTRA:-}" ] && ARGS+=($EXTRA)   # more probe arguments (a trial), as words
. "$(dirname "$0")/lib/barprobe.sh"
