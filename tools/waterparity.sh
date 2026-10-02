#!/usr/bin/env bash
# The wave shader against the C# wave field (#299), windowed: a GPU readback needs a renderer, and
# headless Godot has none. 256 points at the gamey state; fails over 5 mm. Builds no world.
#   tools/waterparity.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/guard.sh"
set -u
GODOT=${GODOT:-godot}
OUT=test_output
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
guard_wait_ram 3 600 || exit 1
guard_run 120 $OUT/waterparity.log "$GODOT" --path . --resolution 320x240 -- --waterparity
code=$?
grep -h "\[waterparity\]" $OUT/waterparity.log
grep -q "RESULT: ok" $OUT/waterparity.log && exit 0
exit $([ $code = 0 ] && echo 1 || echo $code)
