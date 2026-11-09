#!/usr/bin/env bash
# The earth world against the flat one, shot by shot (G2 C6's gate, #1500; docs/notes/general/regression-net.md):
# every view of tests/refshots/views.txt in every style, flat then --frame earth from the same places looking the
# same way (tools/refshot-poses.py), then tools/refshot-diff.py at 1/8 scale with the expected differences of
# tests/refshots/earth-vs-flat.txt, A | B | diff sheets per style and summary.md.
#
#   tools/refshot-parity.sh <tag>        e.g. tools/refshot-parity.sh c6-82e03ec5
#
# Writes $REFSHOTS/<tag>-flat, $REFSHOTS/<tag>-earth and the diff in $REFSHOTS/<tag>-earth/diff-<tag>-flat/
# (summary.md, report.html, sheet-<style>.png). ~50 min for the three styles on a free machine; one heavy-run slot.
# Env: as tools/refshots.sh (STYLES, VIEWS, LEAVES, GLOBE, CHUNKS, GODOT ...), plus
#   NOISE   the floor at 1/8 scale (D:/UnitSportSwitzerland/baselines/dd6b7207/noise-s8.json: two flat runs of one
#           commit; write another with refshot-diff.py A B --scale 8 --write-noise F)
#   SKIP    "flat" retakes only earth against an existing <tag>-flat (a C6 fix that touches earth alone), "shots"
#           only the diff
# Exit: refshot-diff.py's, 1 when a shot not judged is over its floor.
set -uo pipefail
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
tag=${1:?usage: tools/refshot-parity.sh <tag>}
ROOT=${REFSHOTS:-D:/UnitSportSwitzerland/refshots}
NOISE=${NOISE:-D:/UnitSportSwitzerland/baselines/dd6b7207/noise-s8.json}
export GLOBE=${GLOBE:-D:/UnitSportSwitzerland/test_b3/b4/cube_global}   # the same pyramid in both worlds
SKIP=${SKIP:-}
flat=$ROOT/$tag-flat
earth=$ROOT/$tag-earth

if [ "$SKIP" != shots ]; then
  [ -n "${GUARD_LOCK_HELD:-}" ] || { GUARD_WHAT="refshot-parity $tag" guard_lock 7200 10800 || exit 1; }
  trap guard_unlock EXIT
  if [ "$SKIP" != flat ]; then
    ANCHORS=1 tools/refshots.sh --frame flat "$tag-flat" || exit $?
  fi
  [ -d "$flat" ] || { echo "no flat library $flat" >&2; exit 2; }
  HEIGHTS=$flat tools/refshots.sh --frame earth "$tag-earth" || exit $?
  guard_unlock
fi

noise=(); [ -f "$NOISE" ] && noise=(--noise "$NOISE")
python tools/refshot-diff.py "$flat" "$earth" --scale 8 "${noise[@]}" --expect tests/refshots/earth-vs-flat.txt \
  --sheets --all-sheets --out "$earth/diff-$tag-flat"
