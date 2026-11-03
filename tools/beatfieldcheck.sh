#!/usr/bin/env bash
# Beat field (#734): one windowed offline client on the fixture world (nothing reacts
# headless) plays a radio at full volume and checks things near it move with it, drawing only:
# a dropped item, registered meshes, the shader globals, the volume-sized reach (test_output/beatfield.png).
#   tools/beatfieldcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
GODOT=${GODOT:-godot}
OUT=test_output; mkdir -p $OUT
[ -n "$GUARD_LOCK_HELD" ] || { guard_lock; trap guard_unlock EXIT; }
guard_wait_ram 2
guard_test_userdata $OUT/userdata_beatfieldcheck   # its own user:// (#1378): not the player's, no focus, no sound
guard_run 300 $OUT/beatfieldcheck.log ${GUARD_GODOT_ENV[@]+"${GUARD_GODOT_ENV[@]}"} "$GODOT" --path . -- --beatfieldcheck --world fixture
grep -h "\[beatfield\]" $OUT/beatfieldcheck.log
grep -q "\[beatfield\] RESULT: ok" $OUT/beatfieldcheck.log && code=0 || code=1
echo "[beatfieldcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
