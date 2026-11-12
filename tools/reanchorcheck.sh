#!/usr/bin/env bash
# No pop at a re-anchor, on the D: region's leaves (#1710, G2 D0; src/Core/ReanchorCheck): a camera driven 6 km then
# flown 6 km at 300 m in 3 km legs from Geneva, a re-anchor every 500 m, props at rest by the start; windowed by default,
# so every frame round a re-anchor is judged against its neighbours too (HEADLESS=1: positions only).
#   GODOT=<exe> [LEAVES=dir] [CHUNKS=dir] [AT=E,N] [HEADLESS=1] tools/reanchorcheck.sh     output: test_output/reanchorcheck.log
NAME=reanchorcheck
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks}
AT=${AT:-2500956,1117569}   # Geneva, the densest leaf of the D: region
[ -z "${HEADLESS:-}" ] && WINDOWED=1
NEED=("$LEAVES")
TIMEOUT_S=900
ARGS=(--reanchorcheck --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT" --originshift 500 --reanchortravel 6000 --traffic 0)
. "$(dirname "$0")/lib/barprobe.sh"
