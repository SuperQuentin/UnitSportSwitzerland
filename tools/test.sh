#!/usr/bin/env bash
# Tiered test runner (docs/notes/general/testing.md).
#   tools/test.sh unit            tier 0: dotnet test tests/UnitSportSwitzerland.Tests, no Godot
#   tools/test.sh quick [area]    unit + the headless no-map checks the change calls for: tier 0.5
#                                 (--world flat, --systems) and tier 1 (fixture courses), never the map
#   tools/test.sh net [area]      quick + the headless network tier on the fixture world, if the change touches it
#   tools/test.sh full [area]     every check in tools/lib/checkmap.txt, windowed and load scripts too
#   tools/test.sh lock            who holds the machine-wide heavy-run lock, and whether it is stale
# The checks come from tools/lib/checkmap.txt, matched against the files changed since origin/main
# (plus uncommitted and untracked ones); [area] (e.g. Loot, src/Player/) matches it instead.
# Env: GODOT (the Godot executable; on Windows the full path of the *_console.exe, see
#      docs/notes/general/godot-exe.md), TEST_BASE (origin/main), TEST_TIMEOUT (600 s per check),
#      TEST_RAM_GB (3, free RAM before each light Godot run), TEST_HEAVY_RAM_GB (6, before the
#      net/full tiers), TEST_PORT (7821), CHUNKS (passed on to the tools/*check.sh scripts),
#      TEST_REALTIME (1: run the quick tier in real time, without --fixed-fps), TEST_JOBS (net/full checks at
#      once, default the heavy lock's slot count; 1 = one at a time), TEST_ONLY (one tier's rows alone:
#      `TEST_ONLY=net tools/test.sh full` runs the whole net tier).
# Quick-tier Godot checks run with --fixed-fps 60: game time, as fast as the CPU allows
# (docs/notes/general/fast-checks.md); a map row whose check starts with @realtime opts out.
# Prints one PASS/FAIL table; every log goes to test_output/tests/. Exit code 0 only if all passed.
set -u
TIER=${1:-}
AREA=${2:-}
case $TIER in
  unit | quick | net | full | lock) ;;
  *) echo "usage: tools/test.sh unit|quick|net|full [area] | lock" >&2; exit 2 ;;
esac
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
[ "$TIER" = lock ] && { guard_status; exit 0; }

# Windows: the winget `godot`/`godot_console` links start the GUI build and hang headless runs
# (docs/notes/general/godot-exe.md), so default to the real console exe when one is installed
if [ -z "${GODOT:-}" ] && _guard_windows; then
  for g in "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe            /c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe; do
    [ -f "$g" ] && { GODOT=$g; break; }
  done
fi
GODOT=${GODOT:-godot}
export GODOT
OUT=test_output/tests
TIMEOUT=${TEST_TIMEOUT:-600}
# one physics tick (project default 60 Hz) per frame, no real-time sync; net/full stay real time
FAST=--fixed-fps\ 60
[ "${TEST_REALTIME:-}" = 1 ] && FAST=
BASE=${TEST_BASE:-origin/main}
mkdir -p "$OUT"

ROWS=()
FAILED=0
SERVER=
cleanup() {
  [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"
  guard_unlock
}
trap cleanup EXIT

# a check's verdict: its last RESULT line if it printed one (exit 139 at shutdown is ignored,
# docs/notes/general/headless-exit-139.md), its exit code otherwise
verdict() {
  local code=$1 log=$2 res
  res=$(grep -a "RESULT" "$log" 2>/dev/null | tail -1)
  if [ "$code" = 124 ]; then echo TIMEOUT
  elif [ -n "$res" ]; then
    if [[ $res == *FAIL* ]]; then echo FAIL
    elif [ "$code" = 0 ] || [ "$code" = 139 ]; then echo PASS
    else echo FAIL; fi
  elif [ "$code" = 0 ]; then echo PASS
  else echo FAIL; fi
}

record() { # name verdict seconds log
  ROWS+=("$(printf '%-30s %-8s %6s  %s' "$1" "$2" "$3" "$4")")
  [ "$2" = PASS ] || FAILED=1
  echo "[test] $1: $2 (${3}s)"
}

# --- which checks -----------------------------------------------------------------------------
changed_files() {
  { git diff --name-only "$BASE"...HEAD; git diff --name-only HEAD; git ls-files --others --exclude-standard; } 2>/dev/null | grep -v '.uid$' | sort -u
}
rpc_touched() {
  { git diff -U0 "$BASE"...HEAD -- '*.cs'; git diff -U0 HEAD -- '*.cs'; } 2>/dev/null |
    grep -Eq '^[+-].*(\[Rpc|Rpc(Id)?\(|ReplicationConfig|MultiplayerSynchronizer|MultiplayerSpawner)'
}

case $TIER in
  unit) WANT="" ;;
  quick) WANT=" quick " ;;
  net) WANT=" quick net " ;;
  full) WANT=" quick net full " ;;
esac

CHANGED=$(changed_files)
CHECKS=()
RPC=
while read -r prefix tier check; do
  check=${check%$'\r'}  # a CRLF checkout must not glue a CR to the check
  [ -z "$prefix" ] || [[ $prefix == \#* ]] && continue
  [[ $WANT == *" $tier "* ]] || continue
  [ -n "${TEST_ONLY:-}" ] && [ "$tier" != "$TEST_ONLY" ] && continue   # one tier alone, e.g. TEST_ONLY=net with full
  if [ -n "$AREA" ]; then
    # tr, not ${x,,}: macOS ships bash 3.2, where that expansion is a syntax error and the
    # whole area match silently fell through to "no Godot check matches <area>"
    [[ $(printf %s "$prefix" | tr '[:upper:]' '[:lower:]') == *"$(printf %s "$AREA" | tr '[:upper:]' '[:lower:]')"* ]] || continue
  elif [ "$TIER" != full ]; then
    if [ "$prefix" = @rpc ]; then
      [ -z "$RPC" ] && { rpc_touched && RPC=yes || RPC=no; }
      [ "$RPC" = yes ] || continue
    else
      grep -q "^$prefix" <<< "$CHANGED" || continue
    fi
  fi
  printf "%s\n" ${CHECKS[@]+"${CHECKS[@]}"} | grep -Fqx -- "$tier $check" || CHECKS+=("$tier $check")
done < tools/lib/checkmap.txt

# --- tier 0 -----------------------------------------------------------------------------------
t0=$SECONDS
guard_run "$TIMEOUT" "$OUT/unit.log" dotnet test tests/UnitSportSwitzerland.Tests
record "unit (dotnet test)" "$(verdict $? "$OUT/unit.log")" $((SECONDS - t0)) "$OUT/unit.log"
grep -aE "^(Passed|Failed)!" "$OUT/unit.log" | tail -1

# --- Godot tiers ------------------------------------------------------------------------------
netsmoke() { # headless dedicated server on the flat fixture world + a headless client joining and leaving it
  local port=$(( ${TEST_PORT:-7821} + ${GUARD_SLOT:-0} * 1000 )) slog=$OUT/netsmoke_server.log
  "$GODOT" --headless --path . -- --server --port "$port" --world fixture > "$slog" 2>&1 < /dev/null &
  SERVER=$!
  for _ in $(seq 1 600); do
    grep -q "server listening" "$slog" 2>/dev/null && break
    kill -0 "$SERVER" 2>/dev/null || break
    guard_nap 0.2
  done
  guard_run "$TIMEOUT" "$1" "$GODOT" --headless --path . -- --leavecheck connect "127.0.0.1:$port" --world fixture
  local code=$?
  _guard_kill_tree "$SERVER"
  wait "$SERVER" 2>/dev/null
  SERVER=
  return $code
}

# the whole check in the log's name: one flag runs in several worlds
log_of() { local name=${1//[^A-Za-z0-9]/_}; name=${name#"${name%%[!_]*}"}; echo "$OUT/$name.log"; }

run_one() { # tier check log: runs one check, its exit code
  local tier=$1 check=$2 log=$3 eng=
  # shellcheck disable=SC2086  # the map's check is split into words on purpose
  case $check in
    @netsmoke) netsmoke "$log" ;;
    tools/*) guard_run "$TIMEOUT" "$log" bash $check ;;
    @realtime\ *) guard_run "$TIMEOUT" "$log" "$GODOT" --headless --path . -- ${check#@realtime } ;;
    *) [ "$tier" = quick ] && eng=$FAST
       guard_run "$TIMEOUT" "$log" "$GODOT" --headless $eng --path . -- $check ;;
  esac
}

# --- the net and full tiers side by side (#1008) ----------------------------------------------
# Up to TEST_JOBS checks at once (default: the heavy lock's slot count, GUARD_SLOTS), each in its own slot,
# so its ports are moved by GUARD_SLOT * 1000 (tools/lib/twoclient.sh). Two scripts that write the same
# logs (one execs the other) never run together, and the load test and the trailer run alone.
JOBS=${TEST_JOBS:-$GUARD_SLOTS}

group_of() { # the twoclient name a script logs under (rampalong execs rampnet: both "rampnet"), or the check
  local f=${1%% *} n t
  if [ -f "$f" ]; then
    n=$(grep -o 'twoclient.sh" [A-Za-z0-9_]*' "$f" | head -1 | awk '{ print $2 }')
    t=$(grep -o 'exec "$(dirname "$0")/[A-Za-z0-9_]*\.sh"' "$f" | grep -o '[A-Za-z0-9_]*\.sh' | head -1)
    [ -z "$n" ] && [ -n "$t" ] && n=$(grep -o 'twoclient.sh" [A-Za-z0-9_]*' "tools/$t" | head -1 | awk '{ print $2 }')
  fi
  echo "${n:-$1}"
}
alone() { case $1 in tools/loadtest.sh* | tools/trailer.sh*) return 0 ;; esac; return 1; }

heavy_job() { # index tier check: one check in a slot of its own, its row in $OUT/.row.<index>
  local i=$1 tier=$2 check=$3 log t0 res v=
  trap guard_unlock EXIT
  log=$(log_of "$check")
  if ! guard_lock; then res="$(printf '%-30s %-8s %6s  %s' "$check" FAIL 0 "(lock)")"
  elif ! guard_wait_ram "${TEST_HEAVY_RAM_GB:-6}"; then res="$(printf '%-30s %-8s %6s  %s' "$check" FAIL 0 "(not enough RAM)")"
  else
    export GUARD_LOCK_HELD=1
    t0=$SECONDS
    run_one "$tier" "$check" "$log"
    v=$(verdict $? "$log")
    res="$(printf '%-30s %-8s %6s  %s' "$check" "$v" $((SECONDS - t0)) "$log")"
    echo "[test] $check: $v ($((SECONDS - t0))s, slot $GUARD_SLOT)"
  fi
  printf '%s\n%s\n' "${v:-FAIL}" "$res" > "$OUT/.row.$i"   # verdict, then the table row
}

run_heavy() {
  local i n=${#HEAVY[@]} pids=() groups=() k busy entry tier check g t0=$SECONDS
  echo "[test] ${n} net/full checks, up to $JOBS at once"
  rm -f "$OUT"/.row.*
  for (( i = 0; i < n; i++ )); do
    entry=${HEAVY[$i]}; tier=${entry%% *}; check=${entry#* }; g=$(group_of "$check")
    # wait for a free job, for the job of the same group, and for every job before one that runs alone
    while :; do
      busy=0
      for k in ${!pids[@]}; do
        if kill -0 "${pids[$k]}" 2>/dev/null; then
          if alone "$check" || [ "${groups[$k]}" = "$g" ] || alone "${groups[$k]}"; then busy=$JOBS; fi
          busy=$(( busy + 1 ))
        else unset 'pids[k]' 'groups[k]'; fi
      done
      [ "$busy" -lt "$JOBS" ] && break
      guard_nap 0.5
    done
    heavy_job "$i" "$tier" "$check" &
    pids+=($!); groups+=("$(alone "$check" && echo "$check" || echo "$g")")
  done
  wait
  for (( i = 0; i < n; i++ )); do
    if [ -f "$OUT/.row.$i" ]; then ROWS+=("$(sed -n 2p "$OUT/.row.$i")"); [ "$(sed -n 1p "$OUT/.row.$i")" = PASS ] || FAILED=1
    else ROWS+=("$(printf '%-30s %-8s %6s  %s' "${HEAVY[$i]#* }" FAIL 0 "(no result)")"); FAILED=1; fi
  done
  rm -f "$OUT"/.row.*
  echo "[test] the net/full checks took $((SECONDS - t0))s side by side"
}

if [ ${#CHECKS[@]} -gt 0 ]; then
  if ! command -v "$GODOT" > /dev/null; then
    echo "[test] no Godot at '$GODOT': set GODOT (docs/notes/general/godot-exe.md)" >&2
    record "godot" FAIL 0 "-"
  else
    t0=$SECONDS
    guard_run "$TIMEOUT" "$OUT/build.log" dotnet build UnitSportSwitzerland.csproj
    code=$?
    # a fresh worktree has no .godot/ yet: import once, headless
    if [ $code = 0 ] && [ ! -d .godot ]; then
      guard_run "$TIMEOUT" "$OUT/import.log" "$GODOT" --headless --path . --import
      code=$?
    fi
    record "build" "$(verdict $code "$OUT/build.log")" $((SECONDS - t0)) "$OUT/build.log"
    if [ $code = 0 ]; then
      locked=0
      HEAVY=()
      for entry in "${CHECKS[@]}"; do
        tier=${entry%% *}
        check=${entry#* }
        log=$(log_of "$check")
        if [ "$tier" = quick ]; then
          guard_wait_ram "${TEST_RAM_GB:-3}" || { record "$check" FAIL 0 "(not enough RAM)"; continue; }
        elif [ "$JOBS" -gt 1 ]; then
          HEAVY+=("$entry"); continue   # side by side below
        else
          # tier 2/3, TEST_JOBS=1: one at a time, in one slot held for the whole run
          [ $locked = 1 ] || { guard_lock && locked=1 && export GUARD_LOCK_HELD=1; } || { record "$check" FAIL 0 "(lock)"; continue; }
          guard_wait_ram "${TEST_HEAVY_RAM_GB:-6}" || { record "$check" FAIL 0 "(not enough RAM)"; continue; }
        fi
        t0=$SECONDS
        run_one "$tier" "$check" "$log"
        record "$check" "$(verdict $? "$log")" $((SECONDS - t0)) "$log"
      done
      [ ${#HEAVY[@]} -gt 0 ] && run_heavy
    fi
  fi
elif [ "$TIER" != unit ]; then
  echo "[test] no Godot check matches ${AREA:-the changed files}"
fi

echo
printf '%-30s %-8s %6s  %s\n' CHECK RESULT SECONDS LOG
printf '%s\n' "${ROWS[@]}"
exit $FAILED
