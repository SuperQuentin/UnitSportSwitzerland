#!/usr/bin/env bash
# Two bots 300 km apart on one server collide and walk as each does alone (src/Bots/RegionCheck, #1456,
# docs/notes/net/server-regions.md). A dedicated server (--generated-world) runs --regioncheck three times:
# both sites at once (a: the spawn, in the main region; b: 300 km east, in a region of its own), then
# each site alone. At each site a walker walks east into a blocker 4 m ahead; tools/regioncheck-compare.py
# compares each site's tracks with and without the other site (tolerance 1 cm). A second walker at each site
# heads into a car the server parked in its main frame: in the far region it meets the car's copy
# (RegionMirrors). Then the same with --regionenu (#1513): the far region an ENU frame (true north, its own up), the
# far site alone in it, and the hop a turning handover that keeps the walker's heading from true north. BEFORE=1 adds a run with --noregions (every body in the server's one frame: what the far
# frame cost) and one of the far site with --nomirrors (its walker goes through the car).
#   GODOT=<exe> [BEFORE=1] tools/regioncheck.sh      tracks: test_output/regions/*.csv
. "$(dirname "$0")/lib/twoclient.sh" regions
DIR="$OUT/regions"
# the first python that runs (Windows' python3 can be the Store's stub)
PYTHON=; for p in python3 python; do "$p" -c "" 2>/dev/null && { PYTHON=$p; break; }; done
[ -n "$PYTHON" ] || { echo "[regioncheck] RESULT: FAILED (no python to compare the tracks)"; exit 1; }
rm -rf "$DIR"; mkdir -p "$DIR"
PORT=7838
run() {   # <name> <regioncheck args...>
  local name=$1; shift
  tc_godot 300 "$DIR/$name.log" --headless --path . -- --server --generated-world "${NOTERRAIN[@]}" --port $PORT \
      --no-query --regionout "$DIR" --regioncheck "$@"
  grep -h "\[regioncheck\] \(site\|RESULT\)" "$DIR/$name.log" | grep -v " start "
}
run both both --regionhop
run a a
run b b
run both-enu both --regionhop --regionenu
run b-enu b --regionenu
if [ -n "${BEFORE:-}" ]; then
  run noregions both --noregions
  run nomirrors b --nomirrors   # expected to FAIL: the walker goes through the parked car
fi
if tc_ok 5 "$DIR"/both.log "$DIR"/a.log "$DIR"/b.log "$DIR"/both-enu.log "$DIR"/b-enu.log && "$PYTHON" tools/regioncheck-compare.py "$DIR" ${BEFORE:+--before}; then
    echo "[regioncheck] RESULT: ok"; exit 0
fi
echo "[regioncheck] RESULT: FAILED (see $DIR/*.log)"; exit 1
