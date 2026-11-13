#!/usr/bin/env bash
# Trees on the ground after a flight across re-anchors, on the D: data (#1776, src/World/TreeSeatCheck): the earth
# playtest's two sightings, Geneva's leaves after a flight out over the fill (note 26) and the fill north of Riddes
# (note 30). Headless by default; WINDOWED=1 SHOT=test_output/treeseat.png also writes a picture of each measure.
#   GODOT=<exe> [SPOT=geneva|riddes] [LEAVES=dir] [CHUNKS=dir] tools/treeseatcheck.sh
# No SPOT: both, one after the other. Output: test_output/treeseat_<spot>.log
if [ -z "${SPOT:-}" ]; then
  code=0
  for s in geneva riddes; do SPOT=$s bash "$0" || code=1; done
  exit $code
fi
NAME=treeseat
LOGNAME_=treeseat_$SPOT   # one log per spot; the probe tags its lines [treeseat]
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}
case "${SPOT:-geneva}" in
  geneva) FROM=2500956,1117569; TO=2507588,1127696 ;;   # note 26: four re-anchors on the way
  riddes) FROM=2580761,1116825; TO=2579661,1108305 ;;   # note 30: the fill; the scenario's first re-anchor to the note
  *) echo "[$NAME] SPOT is geneva or riddes"; exit 2 ;;
esac
NEED=("$LEAVES" "$CHUNKS")
TIMEOUT_S=600
ARGS=("--treeseatcheck${SHOT:+,$SHOT}" --leaves "$LEAVES" --chunks "$CHUNKS" --at "$FROM" --treeseatto "$TO" --traffic 0)
. "$(dirname "$0")/lib/barprobe.sh"
