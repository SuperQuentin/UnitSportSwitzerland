#!/usr/bin/env bash
# Two bots 300 km apart on one server collide and walk as each does alone (src/Bots/RegionCheck, #1456,
# docs/notes/net/server-regions.md). A dedicated server (--generated-world) runs --regioncheck three times:
# both sites at once (a: the spawn, in the main region; b: 300 km east, in a region of its own), then
# each site alone. At each site a walker walks east into a blocker 4 m ahead; tools/regioncheck-compare.py
# compares each site's tracks with and without the other site (tolerance 1 cm). A second walker at each site
# heads into a car the server parked in its main frame: in the far region it meets the car's copy
# (RegionMirrors). BEFORE=1 adds a run with --noregions (every body in the server's one frame: what the far
# frame cost) and one of the far site with --nomirrors (its walker goes through the car).
# EARTH=1 runs it under --frame earth on built leaves (#1513, G2 C4): the server's main frame and every region an ENU
# frame on EarthWorld; the sites real places inside the leaves (default Geneva and Riddes, ~83 km apart), the hop
# keeping the far walker's true heading within 0.01°. Skipped without the leaves.
#   GODOT=<exe> [BEFORE=1] tools/regioncheck.sh      tracks: test_output/regions/*.csv
#   EARTH=1 [LEAVES=<dir>] [CHUNKS=<dir>] [AT=E,N] [FAR=E,N] tools/regioncheck.sh
. "$(dirname "$0")/lib/twoclient.sh" regions
DIR="$OUT/regions${EARTH:+_earth}"
WORLD=(--generated-world "${NOTERRAIN[@]}")
if [ -n "${EARTH:-}" ]; then
  LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
  CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}
  AT=${AT:-2500956,1117569}     # Geneva, the densest leaf of the D: region
  FAR=${FAR:-2581500,1113300}   # Riddes
  if [ ! -d "$LEAVES" ]; then echo "[regioncheck] no LEAVES with built leaves: skipped"; echo "[regioncheck] RESULT: ok (skipped)"; exit 0; fi
  WORLD=(--frame earth --leaves "$LEAVES" --chunks "$CHUNKS" --regionat "$AT" --regionfar "$FAR")
fi
# the first python that runs (Windows' python3 can be the Store's stub)
PYTHON=; for p in python3 python; do "$p" -c "" 2>/dev/null && { PYTHON=$p; break; }; done
[ -n "$PYTHON" ] || { echo "[regioncheck] RESULT: FAILED (no python to compare the tracks)"; exit 1; }
rm -rf "$DIR"; mkdir -p "$DIR"
PORT=7838
run() {   # <name> <regioncheck args...>
  local name=$1; shift
  tc_godot 300 "$DIR/$name.log" --headless --path . -- --server "${WORLD[@]}" --port $PORT \
      --no-query --regionout "$DIR" --regioncheck "$@"
  grep -h "\[regioncheck\] \(site\|RESULT\)" "$DIR/$name.log" | grep -v " start "
}
run both both --regionhop
run a a
run b b
if [ -n "${BEFORE:-}" ]; then
  run noregions both --noregions
  run nomirrors b --nomirrors   # expected to FAIL: the walker goes through the parked car
fi
if tc_ok 3 "$DIR"/both.log "$DIR"/a.log "$DIR"/b.log && "$PYTHON" tools/regioncheck-compare.py "$DIR" ${BEFORE:+--before}; then
    echo "[regioncheck] RESULT: ok"; exit 0
fi
echo "[regioncheck] RESULT: FAILED (see $DIR/*.log)"; exit 1
