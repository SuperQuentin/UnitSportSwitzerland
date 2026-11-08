#!/usr/bin/env bash
# The earth world on real leaves (#1510, G2 C1 PR 1, src/Terrain/Earth/EarthCheck): one headless client under
# --frame earth on a region's built cube leaves (B4's): collision against the height queries, a dropped ball, the
# ground streaming under a body at 30 m/s for 4.5 km, and heights across the origin's re-anchors. Needs the leaves:
#   GODOT=<exe> LEAVES=D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube [CHUNKS=] [AT=E,N] tools/earthcheck.sh
# output in test_output/earthcheck.log
. "$(dirname "$0")/lib/guard.sh"
GODOT=${GODOT:-godot}
OUT=test_output
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}
AT=${AT:-2500956,1117569}   # Geneva, the densest leaf of the D: region
if [ ! -d "$LEAVES" ]; then echo "[earthcheck] no LEAVES with built leaves: skipped"; echo "[earthcheck] RESULT: ok (skipped)"; exit 0; fi
mkdir -p "$OUT"
UD="$OUT/userdata_earthcheck"
( guard_test_userdata "$UD" ) >/dev/null 2>&1
if _guard_windows; then UDENV=(env APPDATA="$(cygpath -w "$(cd "$UD" && pwd)")"); else UDENV=(env XDG_DATA_HOME="$(cd "$UD" && pwd)"); fi
guard_run 420 "$OUT/earthcheck.log" "${UDENV[@]}" "$GODOT" --headless --path . -- --frame earth --earthcheck \
  --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT"
code=$?
grep -a "\[earthcheck\]" "$OUT/earthcheck.log" | tail -20
exit $code
