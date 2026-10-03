#!/usr/bin/env bash
# Radio sparkles (#387): one windowed offline client on the fixture world (the glints are not built
# headless) stands a radio before the camera, plays the chess type beat and checks the sparkles fade
# in, screenshot on and off the beat (test_output/sparkles_*.png), and are gone after Stop.
#   tools/sparklecheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
GODOT=${GODOT:-godot}
OUT=test_output; mkdir -p $OUT
[ -n "$GUARD_LOCK_HELD" ] || { guard_lock; trap guard_unlock EXIT; }
guard_wait_ram 2
guard_run 300 $OUT/sparklecheck.log "$GODOT" --path . -- --sparklecheck --world fixture
grep -h "\[sparkles\]" $OUT/sparklecheck.log
grep -q "\[sparkles\] RESULT: ok" $OUT/sparklecheck.log && code=0 || code=1
echo "[sparklecheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
