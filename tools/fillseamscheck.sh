#!/usr/bin/env bash
# The earth fill's seams (#1778, #1779; BlendCheck --fill-seams, docs/notes/terrain/fill-on-pyramid.md): at the spots
# the Nov 12 playtest found walls north of Riddes, the pyramid the fill stands on has no step between neighbouring
# samples, the fill leaves the game builds agree on every edge, the globe's far ground meets them, and the pyramid's
# coarser levels (what the globe draws past the leaves) carry no ledge along the leaves' edges. Godot-free, ~1 min.
# Needs the full pyramid and B4's leaves on D: (skipped without them):
#   PYRAMID=D:/UnitSportSwitzerland/cube_global LEAVES=D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube tools/fillseamscheck.sh
# BlendCheck is built outside the worktree ($TEMP): a build inside the project broke the game's debug trails.
# output in test_output/fillseamscheck.log
. "$(dirname "$0")/lib/guard.sh"
OUT=test_output
PYRAMID=${PYRAMID:-D:/UnitSportSwitzerland/cube_global}
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
if [ ! -d "$PYRAMID" ] || [ ! -d "$LEAVES" ]; then echo "[fillseams] no PYRAMID or LEAVES: skipped"; echo "[fillseams] RESULT: ok (skipped)"; exit 0; fi
mkdir -p "$OUT"
ART="${TEMP:-/tmp}/unitsport-blendcheck-$(basename "$(pwd)")"
dotnet build tools/BlendCheck -c Release -v q -nologo --artifacts-path "$ART" > "$OUT/fillseamscheck-build.log" 2>&1 \
  || { tail -20 "$OUT/fillseamscheck-build.log"; echo "[fillseams] RESULT: FAIL (build)"; exit 1; }
EXE="$ART/bin/BlendCheck/release/BlendCheck"
[ -f "$EXE.exe" ] && EXE="$EXE.exe"
guard_run 600 "$OUT/fillseamscheck.log" nice -n 10 "$EXE" --fill-seams --pyramid "$PYRAMID" --leaves "$LEAVES"
code=$?
grep -a "FAIL\|ok  \|RESULT\|skipped" "$OUT/fillseamscheck.log" | tail -30
exit $code
