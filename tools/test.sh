#!/usr/bin/env bash
# Tiered test runner (docs/notes/general/testing.md).
#   tools/test.sh unit            tier 0: dotnet test tests/UnitSportSwitzerland.Tests, no Godot
#   tools/test.sh quick [area]    unit + the headless no-map checks the change calls for: tier 0.5
#                                 (--world flat, --systems) and tier 1 (fixture courses), never the map
#   tools/test.sh net [area]      quick + the headless network tier on the fixture world, if the change touches it
#   tools/test.sh full [area]     every check in tools/lib/checkmap.txt, windowed and load scripts too
#   tools/test.sh lock            who holds the machine-wide heavy-run lock, and whether it is stale
#   tools/test.sh stop            stops the running test.sh of this checkout and everything under it, by PID (#1409)
# The checks come from tools/lib/checkmap.txt, matched against the files changed since origin/main
# (plus uncommitted and untracked ones); [area] (e.g. Loot, src/Player/) matches it instead.
# Env: GODOT (the Godot executable; on Windows the full path of the *_console.exe, see
#      docs/notes/general/godot-exe.md), TEST_BASE (origin/main), TEST_TIMEOUT (600 s per check),
#      TEST_RAM_GB (3, free RAM before each light Godot run), TEST_HEAVY_RAM_GB (6, before the
#      net/full tiers), TEST_PORT (7821), CHUNKS (passed on to the tools/*check.sh scripts),
#      TEST_REALTIME (1: run the quick tier in real time, without --fixed-fps), TEST_JOBS (net/full checks at
#      once, default the heavy lock's slot count; 1 = one at a time), TEST_ONLY (one tier's rows alone:
#      `TEST_ONLY=net tools/test.sh full` runs the whole net tier), TEST_USERDATA (real: the Godot checks
#      run on the real user://, not a fresh test_output/tests/userdata/<check> each, #1403), TEST_RESUME (1:
#      skip what results.tsv already holds as PASS, to go on after a run that died, #1409).
# Every result is appended to test_output/tests/results.tsv as it lands (time, check, verdict, seconds, log):
# a run that dies keeps what it had.
# Quick-tier Godot checks run with --fixed-fps 60: game time, as fast as the CPU allows
# (docs/notes/general/fast-checks.md); a map row whose check starts with @realtime opts out.
# Prints one PASS/FAIL table; every log goes to test_output/tests/. Exit code 0 only if all passed.
set -u
TIER=${1:-}
AREA=${2:-}
case $TIER in
  unit | quick | net | full | lock | stop) ;;
  *) echo "usage: tools/test.sh unit|quick|net|full [area] | lock | stop" >&2; exit 2 ;;
esac
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
[ "$TIER" = lock ] && { guard_status; exit 0; }
OUT=test_output/tests
PIDFILE=$OUT/test.pid
# stop (#1409): the run's whole tree from its PID file, never by name; stopping the shell that started it
# left test.sh and its checks running before
# A child that forks while the tree is walked is missed by the walk, and orphaned once its parent dies: every
# process of a run inherits TEST_RUN (exported below), so a sweep of /proc/*/environ for that exact id finds
# them, and nothing else (Git Bash and Linux; macOS has no /proc: the tree only).
run_procs() { # id: the PIDs still carrying TEST_RUN=<id>
  local e p
  for e in /proc/[0-9]*/environ; do
    [ -r "$e" ] || continue
    p=${e#/proc/}; p=${p%/environ}
    [ "$p" = $$ ] && continue
    tr '\0' '\n' < "$e" 2>/dev/null | grep -qx "TEST_RUN=$1" && echo "$p"
  done
}
if [ "$TIER" = stop ]; then
  pid=$(sed -n 1p "$PIDFILE" 2>/dev/null); run=$(sed -n 2p "$PIDFILE" 2>/dev/null)
  if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
    left=$([ -n "$run" ] && [ -d /proc ] && run_procs "$run")
    [ -z "$left" ] && { echo "[test] no run of this checkout is going"; rm -f "$PIDFILE"; exit 0; }
  fi
  echo "[test] stopping test.sh ${pid:-?} (run $run) and everything under it"
  [ -n "$pid" ] && _guard_kill_tree "$pid"
  for _ in 1 2 3; do   # late forks and orphans, by the run's id
    left=$([ -n "$run" ] && [ -d /proc ] && run_procs "$run")
    [ -z "$left" ] && break
    for p in $left; do _guard_kill_tree "$p"; done
    sleep 1
  done
  left=$([ -n "$run" ] && [ -d /proc ] && run_procs "$run")
  rm -f "$PIDFILE"
  [ -z "$left" ] && echo "[test] stopped: nothing of it is left" || echo "[test] still there, by PID: $left"
  exit 0
fi

# Windows: the winget `godot`/`godot_console` links start the GUI build and hang headless runs
# (docs/notes/general/godot-exe.md), so default to the real console exe when one is installed
if [ -z "${GODOT:-}" ] && _guard_windows; then
  for g in "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe            /c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe; do
    [ -f "$g" ] && { GODOT=$g; break; }
  done
fi
GODOT=${GODOT:-godot}
export GODOT
TIMEOUT=${TEST_TIMEOUT:-600}
# one physics tick (project default 60 Hz) per frame, no real-time sync; net/full stay real time
FAST=--fixed-fps\ 60
[ "${TEST_REALTIME:-}" = 1 ] && FAST=
BASE=${TEST_BASE:-origin/main}
mkdir -p "$OUT"
export TEST_RUN="$$.$RANDOM$RANDOM"   # every process of this run inherits it: what `stop` sweeps for
printf '%s\n%s\n' $$ "$TEST_RUN" > "$PIDFILE"
RESULTS=$OUT/results.tsv
[ "${TEST_RESUME:-}" = 1 ] && [ -f "$RESULTS" ] || printf 'time\tcheck\tverdict\tseconds\tlog\n' > "$RESULTS"

ROWS=()
FAILED=0
SERVER=
cleanup() {
  [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"
  guard_unlock
  [ "$(sed -n 1p "$PIDFILE" 2>/dev/null)" = $$ ] && rm -f "$PIDFILE"
}
trap cleanup EXIT

# one line of results.tsv, appended as it lands (from heavy jobs too: one short line, one write)
result_line() { # name verdict seconds log
  printf '%s\t%s\t%s\t%s\t%s\n' "$(date '+%F %T')" "$1" "$2" "$3" "$4" >> "$RESULTS"
}
# TEST_RESUME=1: this check passed earlier in the results.tsv being continued
passed_before() {
  [ "${TEST_RESUME:-}" = 1 ] && grep -qF "$(printf '\t%s\tPASS\t' "$1")" "$RESULTS"
}

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
  result_line "$1" "$2" "$3" "$4"
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

# A --collidecheck row whose targets another selected row already covers, in the same world, runs once (#1407):
# several areas list the combined runs and the per-folder ones, and a full run rammed every truck, boat and
# plane two or three times (11 runs, 1,014 s). Targets compare without case ("Trucks/" = "trucks/").
collide_parts() { # check: sets C_WORLD (every word but the targets) and C_TARGETS (",a/,b/,", lower case)
  local w words=() t=
  read -r -a words <<< "$1"
  C_WORLD=
  for ((w = 0; w < ${#words[@]}; w++)); do
    if [ "${words[w]}" = --targets ]; then t=${words[w + 1]:-}; w=$((w + 1)); else C_WORLD="$C_WORLD ${words[w]}"; fi
  done
  C_TARGETS=",$(printf %s "$t" | tr '[:upper:]' '[:lower:]'),"
}
collide_covers() { # big small (",a/,b/," lists): every target of small is in big
  local x rest=${2#,}
  while [ -n "$rest" ]; do
    x=${rest%%,*}; rest=${rest#*,}
    [ -z "$x" ] && continue
    [[ $1 == *",$x,"* ]] || return 1
  done
  return 0
}
KEPT=()
for ((i = 0; i < ${#CHECKS[@]}; i++)); do
  entry=${CHECKS[i]}; tier=${entry%% *}; check=${entry#* }
  if [[ $check == --collidecheck* ]] && [[ $check == *--targets* ]]; then
    collide_parts "$check"; mine_world=$C_WORLD; mine=$C_TARGETS; covered=
    for ((j = 0; j < ${#CHECKS[@]}; j++)); do
      [ "$j" = "$i" ] && continue
      other=${CHECKS[j]#* }; [ "${CHECKS[j]%% *}" = "$tier" ] || continue
      [[ $other == --collidecheck* ]] && [[ $other == *--targets* ]] || continue
      collide_parts "$other"
      [ "$C_WORLD" = "$mine_world" ] || continue
      collide_covers "$C_TARGETS" "$mine" || continue
      # the same set twice in other spellings: keep the first
      if collide_covers "$mine" "$C_TARGETS" && [ "$j" -gt "$i" ]; then continue; fi
      covered=yes; break
    done
    [ -n "$covered" ] && { echo "[test] skipped $check: another selected collidecheck covers its targets"; continue; }
  fi
  KEPT+=("$entry")
done
CHECKS=(${KEPT[@]+"${KEPT[@]}"})

# --- tier 0 -----------------------------------------------------------------------------------
# a row TEST_RESUME skips: in the table as passed earlier, not again in results.tsv
resumed() { ROWS+=("$(printf '%-30s %-8s %6s  %s' "$1" PASS 0 "(passed earlier, TEST_RESUME)")"); echo "[test] $1: passed earlier, skipped"; }
if passed_before "unit (dotnet test)"; then resumed "unit (dotnet test)"; else
  t0=$SECONDS
  # [Trait("Speed", "Slow")] tests run in the full tier only (#1407): the signal test region's build alone was
  # 556 s of a 14 min unit stage
  SLOW=(--filter "Speed!=Slow"); [ "$TIER" = full ] && SLOW=()
  guard_run "$TIMEOUT" "$OUT/unit.log" dotnet test tests/UnitSportSwitzerland.Tests ${SLOW[@]+"${SLOW[@]}"}
  record "unit (dotnet test)" "$(verdict $? "$OUT/unit.log")" $((SECONDS - t0)) "$OUT/unit.log"
  grep -aE "^(Passed|Failed)!" "$OUT/unit.log" | tail -1
fi
# RideLink (#901, tools/RideLink): the trainer library's own tests (parsers, the FTMS handshake, the router)
if passed_before "unit RideLink (dotnet test)"; then resumed "unit RideLink (dotnet test)"; else
  t0=$SECONDS
  guard_run "$TIMEOUT" "$OUT/ridelink.log" dotnet test tools/RideLink/tests/RideLink.Tests
  record "unit RideLink (dotnet test)" "$(verdict $? "$OUT/ridelink.log")" $((SECONDS - t0)) "$OUT/ridelink.log"
fi

# --- Godot tiers ------------------------------------------------------------------------------
# Every Godot here runs in a fresh user:// of its own check (#1403): the player's settings, CDs and placed
# objects never reach a check (an H-pattern truck gearbox in the real one failed seven). UD is the env prefix
# for one run, never exported, so nothing after it inherits the folder; TEST_USERDATA=real keeps the real one.
ud() { # log path: sets UD
  UD=()
  [ "${TEST_USERDATA:-}" = real ] && return 0
  local d
  d="$OUT/userdata/$(basename "$1" .log)"
  ( guard_test_userdata "$d" ) || return 0   # the folder and its quiet test_window.cfg, nothing exported here
  d=$(cd "$d" && pwd)
  if _guard_windows; then UD=(env APPDATA="$(cygpath -w "$d")")
  elif [ "$(uname)" = Darwin ]; then UD=(env HOME="$d")
  else UD=(env XDG_DATA_HOME="$d"); fi
}

netsmoke() { # headless dedicated server on the flat fixture world + a headless client joining and leaving it
  local port=$(( ${TEST_PORT:-7821} + ${GUARD_SLOT:-0} * 1000 )) slog=$OUT/netsmoke_server.log
  ud "$1"   # the server and the client share one user://, as a twoclient check's do
  ${UD[@]+"${UD[@]}"} "$GODOT" --headless ${TC_MAX_FPS:+--max-fps $TC_MAX_FPS} --path . -- --server --port "$port" --world fixture > "$slog" 2>&1 < /dev/null &
  SERVER=$!
  for _ in $(seq 1 600); do
    grep -q "server listening" "$slog" 2>/dev/null && break
    kill -0 "$SERVER" 2>/dev/null || break
    guard_nap 0.2
  done
  guard_run "$TIMEOUT" "$1" ${UD[@]+"${UD[@]}"} "$GODOT" --headless ${TC_MAX_FPS:+--max-fps $TC_MAX_FPS} --path . -- --leavecheck connect "127.0.0.1:$port" --world fixture
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
    @realtime\ *) ud "$log"; guard_run "$TIMEOUT" "$log" ${UD[@]+"${UD[@]}"} "$GODOT" --headless --path . -- ${check#@realtime } ;;
    *) if [ "$tier" = quick ]; then eng=$FAST; elif [ -n "${TC_MAX_FPS:-}" ]; then eng="--max-fps $TC_MAX_FPS"; fi
       ud "$log"; guard_run "$TIMEOUT" "$log" ${UD[@]+"${UD[@]}"} "$GODOT" --headless $eng --path . -- $check ;;
  esac
}

# --- the net and full tiers side by side (#1008) ----------------------------------------------
# Up to TEST_JOBS checks at once (default: the heavy lock's slot count, GUARD_SLOTS), each in its own slot,
# so its ports are moved by GUARD_SLOT * 1000 (tools/lib/twoclient.sh). Two scripts that write the same
# logs (one execs the other) never run together, and the load test and the trailer run alone.
# A job starts only with TEST_HEAVY_RAM_GB (6) free and TEST_JOB_STAGGER (40) s after the last one (its Godots grow
# for that long). Beside others every headless Godot runs at TEST_MAX_FPS (60), and a check that failed or hit the RAM
# floor runs again alone at the end, uncapped; a pass alone says it failed beside others (#1081).
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
  local i=$1 tier=$2 check=$3 log t0 res v= ram=0
  trap guard_unlock EXIT
  log=$(log_of "$check")
  if ! guard_lock; then res="$(printf '%-30s %-8s %6s  %s' "$check" FAIL 0 "(lock)")"
  elif ! guard_wait_ram "${TEST_HEAVY_RAM_GB:-6}"; then res="$(printf '%-30s %-8s %6s  %s' "$check" FAIL 0 "(not enough RAM)")"
  else
    export GUARD_LOCK_HELD=1
    t0=$SECONDS
    run_one "$tier" "$check" "$log"
    v=$(verdict $? "$log")
    # stopped by the RAM watchdog, not by what it checks: run again alone at the end
    grep -aq "RAM floor\|MB free (floor" "$log" && ram=1
    res="$(printf '%-30s %-8s %6s  %s' "$check" "$v" $((SECONDS - t0)) "$log")"
    result_line "$check" "$v" $((SECONDS - t0)) "$log"
    echo "[test] $check: $v ($((SECONDS - t0))s, slot $GUARD_SLOT$([ $ram = 1 ] && echo ", stopped at the RAM floor"))"
  fi
  printf '%s\n%s\n%s\n' "${v:-FAIL}" "$res" "$ram" > "$OUT/.row.$i"   # verdict, table row, RAM-floor stop
}

# room for one more job: free RAM for it, and the last one started TEST_JOB_STAGGER s ago (40), so its Godots
# have grown to their size before the next is weighed against what is left
room() { [ $(( SECONDS - last_start )) -ge "${TEST_JOB_STAGGER:-40}" ] && _guard_free_into && [ "$GUARD_FREE" -ge $(( ${TEST_HEAVY_RAM_GB:-6} * 1024 )) ]; }

run_heavy() {
  local i n=${#HEAVY[@]} pids=() groups=() k busy running entry tier check g t0=$SECONDS last_start=-999 again=()
  echo "[test] ${n} net/full checks, up to $JOBS at once"
  rm -f "$OUT"/.row.*
  for (( i = 0; i < n; i++ )); do
    entry=${HEAVY[$i]}; tier=${entry%% *}; check=${entry#* }; g=$(group_of "$check")
    # wait for a free job and room for it, for the job of the same group, and for every job before one that runs alone
    while :; do
      busy=0; running=0
      for k in ${!pids[@]}; do
        if kill -0 "${pids[$k]}" 2>/dev/null; then
          running=$(( running + 1 ))
          if alone "$check" || [ "${groups[$k]}" = "$g" ] || alone "${groups[$k]}"; then busy=$JOBS; fi
          busy=$(( busy + 1 ))
        else unset 'pids[k]' 'groups[k]'; fi
      done
      [ "$busy" -lt "$JOBS" ] && { [ "$running" = 0 ] || room; } && break
      guard_nap 1
    done
    last_start=$SECONDS
    # beside others, every headless Godot at TEST_MAX_FPS (60): one frame per physics tick, about half the CPU of
    # an uncapped real-time run, the physics the same (#1081)
    ( export TC_MAX_FPS=${TEST_MAX_FPS:-60}; heavy_job "$i" "$tier" "$check" ) &
    pids+=($!); groups+=("$(alone "$check" && echo "$check" || echo "$g")")
  done
  wait
  # stopped at the RAM floor, or failed beside others: again alone at the end, uncapped, as a serial run would (#1081).
  # A pass alone is reported as one, saying it failed beside others; a fail alone is a fail. TEST_RETRY_ALONE=0: RAM only
  local why
  for (( i = 0; i < n; i++ )); do
    [ "$(sed -n 3p "$OUT/.row.$i" 2>/dev/null)" = 1 ] && { again+=("$i"); continue; }
    [ "${TEST_RETRY_ALONE:-1}" != 0 ] && [ -f "$OUT/.row.$i" ] && [ "$(sed -n 1p "$OUT/.row.$i")" != PASS ] && again+=("$i")
  done
  for i in ${again[@]+"${again[@]}"}; do
    entry=${HEAVY[$i]}
    why="failed beside the others"; [ "$(sed -n 3p "$OUT/.row.$i")" = 1 ] && why="stopped at the RAM floor beside the others"
    echo "[test] again, alone: ${entry#* } ($why)"
    ( heavy_job "$i" "${entry%% *}" "${entry#* }" )   # a subshell: its EXIT trap must not replace the runner's
    [ "$why" = "failed beside the others" ] && [ "$(sed -n 1p "$OUT/.row.$i")" = PASS ] &&
      sed -i '2s|$|  (passed alone, failed beside others)|' "$OUT/.row.$i"
  done
  for (( i = 0; i < n; i++ )); do
    if [ -f "$OUT/.row.$i" ]; then ROWS+=("$(sed -n 2p "$OUT/.row.$i")"); [ "$(sed -n 1p "$OUT/.row.$i")" = PASS ] || FAILED=1
    else ROWS+=("$(printf '%-30s %-8s %6s  %s' "${HEAVY[$i]#* }" FAIL 0 "(no result)")"); FAILED=1; fi
  done
  rm -f "$OUT"/.row.*
  echo "[test] the net/full checks took $((SECONDS - t0))s side by side (${#again[@]} run again alone)"
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
        passed_before "$check" && { resumed "$check"; continue; }
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
