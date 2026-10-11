#!/usr/bin/env bash
# Several radios at once (#813): one windowed offline client on the fixture world (radios get no
# speaker headless) plays two radios, one near the ears and one far; the far one must duck under the
# near one, and come back up when the near one stops. Read the [radiofocus] lines.
#   tools/radiofocuscheck.sh       (GODOT = the editor executable, docs/notes/general/godot-exe.md)
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
GODOT=${GODOT:-godot}
OUT=test_output; mkdir -p $OUT
[ -n "$GUARD_LOCK_HELD" ] || { guard_lock; trap guard_unlock EXIT; }
guard_wait_ram 2
guard_run 300 $OUT/radiofocuscheck.log "$GODOT" --path . -- --radiofocuscheck --world fixture
grep -h "\[radiofocus\]" $OUT/radiofocuscheck.log
grep -q "\[radiofocus\] RESULT: ok" $OUT/radiofocuscheck.log && code=0 || code=1
echo "[radiofocuscheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
