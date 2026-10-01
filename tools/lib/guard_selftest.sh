#!/usr/bin/env bash
# Self-test of tools/lib/guard.sh's lock (no Godot, ~1 min): a lock whose owner died, whose heartbeat
# stopped, or which is held past its max is taken over at once; a live lock is waited for.
# Usage: bash tools/lib/guard_selftest.sh   -> prints RESULT: ok / RESULT: FAILED
cd "$(dirname "$0")/../.." || exit 1
export GUARD_LOCK_DIR="${TMPDIR:-${TEMP:-/tmp}}/unitsport-guard-selftest.$$.lock" GUARD_BEAT=1 GUARD_STALE=4
. tools/lib/guard.sh
fails=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fails=$((fails + 1)); fi; }
took() { local t0=$SECONDS; ( guard_lock 30 && guard_unlock ) 2>/dev/null; echo $(( SECONDS - t0 )); }

# 1. owner killed without unlocking -> taken over without waiting for the heartbeat
( guard_lock; kill -9 $BASHPID ) 2>/dev/null; sleep 1
check "dead owner taken over at once" '[ "$(took)" -le 3 ]'

# 2. owner alive but heartbeat stopped (hung, or its PID reused) -> taken over after GUARD_STALE
( guard_lock; kill "$_guard_beat_pid"; sleep 30 ) & hung=$!; sleep 2
check "stalled heartbeat taken over within GUARD_STALE" '[ "$(took)" -le 8 ]'
kill $hung 2>/dev/null; wait $hung 2>/dev/null

# 3. owner alive and beating but past max_hold + 120 s -> taken over
( guard_lock 10 1; sleep 30 ) & over=$!; sleep 2
sed -i "s/^started=.*/started=$(( $(date +%s) - 200 ))/" "$GUARD_LOCK_DIR/info"
check "held past its max taken over" '[ "$(took)" -le 3 ]'
kill $over 2>/dev/null; wait $over 2>/dev/null

# 4. a live, beating owner is waited for, then the lock passes on
( guard_lock; sleep 6; guard_unlock ) & live=$!; sleep 1
t=$(took)
check "live lock waited for (${t}s)" '[ "$t" -ge 4 ] && [ "$t" -le 12 ]'
wait $live 2>/dev/null

_guard_drop_lock
[ "$fails" -eq 0 ] && echo "RESULT: ok" || echo "RESULT: FAILED ($fails)"
