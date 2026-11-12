# One G2 D bar probe (#1710) on the D: data: a Godot of its own under --frame earth (or FRAME), quiet (its own user://), stoppable,
# under the RAM guard; skipped (RESULT ok) when its data is not on this machine. Sourced by tools/reanchorcheck.sh,
# tools/longdrivecheck.sh and tools/longflightcheck.sh, which set NAME, TIMEOUT_S, NEED (dirs that must exist) and ARGS.
#   WINDOWED=1: windowed (the picture judged across every re-anchor too); headless otherwise
#   FRAME=flat|earth: the frame it runs in, earth unless the script says otherwise (the long drive runs in TEST_FRAME's)
# The log: test_output/<name>.log. Its last RESULT line is the probe's, then the frame check's if it fails.
. "$(dirname "${BASH_SOURCE[0]}")/guard.sh"
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
GODOT=${GODOT:-godot}
OUT=test_output
mkdir -p "$OUT"
LOG=$OUT/$NAME.log
for d in ${NEED[@]+"${NEED[@]}"}; do
  if [ ! -d "$d" ]; then echo "[$NAME] no $d on this machine: skipped"; echo "[$NAME] RESULT: ok (skipped: no data)"; exit 0; fi
done
[ -n "${GUARD_LOCK_HELD:-}" ] || { guard_lock 1800 $((TIMEOUT_S + 120)) || exit 1; trap guard_unlock EXIT; }
guard_wait_ram 4 1800 || { echo "[$NAME] RESULT: FAILED (not enough RAM)"; exit 1; }
guard_test_userdata "$OUT/userdata_$NAME"   # its own user:// (#1378): not the player's, no focus, no sound
HEAD=--headless; [ -n "${WINDOWED:-}" ] && HEAD=
# shellcheck disable=SC2086
guard_run "$TIMEOUT_S" "$LOG" ${GUARD_GODOT_ENV[@]+"${GUARD_GODOT_ENV[@]}"} "$GODOT" $HEAD --path . -- --frame "${FRAME:-earth}" "${ARGS[@]}"
code=$?
grep -a "\[$NAME\]" "$LOG" | tail -25
# its log states the frame it was run in, whatever TEST_FRAME is (#1710)
TEST_FRAME=${FRAME:-earth} guard_frame_check "$NAME" "$LOG" || code=1
exit $code
