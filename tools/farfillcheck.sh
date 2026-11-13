#!/usr/bin/env bash
# The fill far from Switzerland against the globe round it (#1800, src/Terrain/Earth/FarFillCheck): a camera over the
# generated fill on another cube face (default: Ladakh, where the user came down from Geneva), the globe's drawn ground
# and lakes read against the near leaves' ground over a 600 m grid; it failed with the pyramid's level-4 tile drawn up to
# 317 m over the leaves. Also the world's up against the ellipsoid's at the camera (a rolled view).
#   GODOT=<exe> [AT=E,N] [WINDOWED=1 SHOTS=dir] tools/farfillcheck.sh
# Other spots (LV95 notation, past Switzerland too): Everest 9554929,2509902; Colorado -5211876,6256180 (face F4);
# Pacific -13287549,7940380; Brazil -6009819,-3052261 (F3); Japan 11888869,8015844. The sea (#1808: the leaves must be water
# over the pyramid's sea): open sea 426597,-230351, the coast at Agadir 909680,-472439.
# output: test_output/farfillcheck.log
NAME=farfillcheck
GLOBE=${GLOBE:-D:/UnitSportSwitzerland/cube_global}
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
AT=${AT:-8383454,2168404}
NEED=("$GLOBE" "$LEAVES")
TIMEOUT_S=${TIMEOUT_S:-400}
ARGS=(--farfillcheck --globe "$GLOBE" --leaves "$LEAVES" --at "$AT" --traffic 0)
[ -n "${SHOTS:-}" ] && ARGS+=(--farfillshot "$SHOTS")
. "$(dirname "$0")/lib/barprobe.sh"
