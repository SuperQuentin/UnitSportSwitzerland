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
AT=${AT:-2500956,1117569}   # Geneva
TO=${TO:-2507500,1138500}   # Nyon, the Geneva patch's north end along the lake
NEED=("$LEAVES")
FRAME=${FRAME:-${TEST_FRAME:-earth}}
TIMEOUT_S=${TIMEOUT_S:-3600}
ARGS=(--longdrivecheck --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT" --to "$TO" --car "${CAR:-4}" --traffic 0)
. "$(dirname "$0")/lib/barprobe.sh"
