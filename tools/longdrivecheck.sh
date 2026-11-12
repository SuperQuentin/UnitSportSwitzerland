#!/usr/bin/env bash
# The long drive (#1710, G2 D0; src/Player/LongDriveCheck): a car on its autopilot down the lane graph from
# Versoix to Nyon on the D: region's leaves (~15 km of road, legs of 3 km), across ~8 re-anchors and many leaves: no stall, no
# fall, no jump. Real time (the leaves stream on threads), ~25 min.
#   GODOT=<exe> [LEAVES=dir] [CHUNKS=dir] [AT=E,N] [TO=E,N] [CAR=n] [WINDOWED=1] tools/longdrivecheck.sh
# output: test_output/longdrivecheck.log
NAME=longdrivecheck
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks}
AT=${AT:-2502300,1125900}   # Versoix (from Geneva's centre the racing pilot went into the first wall, flat and earth alike)
TO=${TO:-2507500,1138500}   # Nyon, the Geneva patch's north end along the lake
NEED=("$LEAVES")
TIMEOUT_S=${TIMEOUT_S:-3600}
ARGS=(--longdrivecheck --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT" --to "$TO" --car "${CAR:-4}" --traffic 0)
. "$(dirname "$0")/lib/barprobe.sh"
