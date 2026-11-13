#!/usr/bin/env bash
# Tiered test runner (docs/notes/general/testing.md).
#   tools/test.sh unit            tier 0: dotnet test tests/UnitSportSwitzerland.Tests, no Godot
#   tools/test.sh quick [area]    unit + the headless no-map checks the change calls for: tier 0.5
#                                 (--world flat, --systems) and tier 1 (fixture courses), never the map
#   tools/test.sh net [area]      quick + the headless network tier on the fixture world, if the change touches it
#   tools/test.sh full [area]     every check in tools/lib/checkmap.txt, windowed and load scripts too
#   tools/test.sh coords          every row of every tier whose prefix covers code that uses the coordinate
#                                 system: the baseline before a coordinate or rendering change (#1429)
#   tools/test.sh lock            who holds the machine-wide heavy-run lock, and whether it is stale
#   tools/test.sh stop            stops the running test.sh of this checkout and everything under it, by PID (#1409)
# The checks come from tools/lib/checkmap.txt, matched against the files changed since origin/main
# (plus uncommitted and untracked ones); [area] (e.g. Loot, src/Player/) matches it instead.
# Env: GODOT (the Godot executable; on Windows the full path of the *_console.exe, see
#      docs/notes/general/godot-exe.md), TEST_BASE (origin/main), TEST_TIMEOUT (600 s per check),
#      TEST_RAM_GB (3, free RAM for a quick check never measured), TEST_HEAVY_RAM_GB (6, for a net/full
#      one), TEST_PORT (7821), CHUNKS (passed on to the tools/*check.sh scripts),
#      TEST_REALTIME (1: run the quick tier in real time, without --fixed-fps), TEST_JOBS (net/full checks at
#      once, default the heavy lock's slot count; 1 = one at a time), TEST_QUICK_JOBS (quick checks at once,
#      default 3/8 of the cores and one per 5 GB, 1 to 8; 1 = one at a time; TEST_QUICK_SERIAL: flags whose rows never overlap), TEST_UNIT_BESIDE (0: the unit
#      stage first, not beside the Godot checks), TEST_PEAKS (the machine-wide store of each check's measured peak
#      RAM, default $TEMP/unitsport-check-peaks.tsv, #1702), TEST_ONLY (one tier's rows alone:
#      `TEST_ONLY=net tools/test.sh full` runs the whole net tier), TEST_LEDGER (the results CSV, default
#      test_output/tests/results.csv; tools/test-compare.py compares two), TEST_LIST (1: print the checks
#      it would run, then stop), TEST_USERDATA (real: the Godot checks run on the real user://, not a
#      fresh test_output/tests/userdata/<check> each, #1403), TEST_RESUME (1: skip what results.tsv
#      already holds as PASS, to go on after a run that died, #1409), TEST_FRAME (earth|flat: every Godot of
#      every row plays in that frame, quick, net, full and coords alike: a row without its own --frame gets
#      `--frame <it>`, the tools/*.sh scripts pass it to theirs, and a log that does not state it ("[frame] earth",
#      or "[frame] none" from a process with no world) fails its row, so a silent flat fallback never passes;
#      #1683, #1710; `TEST_FRAME=earth tools/test.sh coords` is G2 D's bar).
# Every result is appended to test_output/tests/results.tsv as it lands (time, check, verdict, seconds, log, peak MB):
# a run that dies keeps what it had.
# Quick-tier Godot checks run with --fixed-fps 60: game time, as fast as the CPU allows
# (docs/notes/general/fast-checks.md); a map row whose check starts with @realtime opts out.
# Prints one PASS/FAIL table; every log goes to test_output/tests/. Exit code 0 only if all passed.
set -u
TIER=${1:-}
AREA=${2:-}
case $TIER in
  unit | quick | net | full | coords | lock | stop) ;;
  *) echo "usage: tools/test.sh unit|quick|net|full [area] | coords | lock | stop" >&2; exit 2 ;;
esac
cd "$(dirname "$0")/.."
_slots_given=${GUARD_SLOTS:-}
. tools/lib/guard.sh
[ "$TIER" = lock ] && { guard_status; exit 0; }
# Net/full slots (#1702): the runner admits each job by its measured peak (need_mb below), so it takes one slot per
# 8 GB, 1 to 4 (32 GB: 4), not guard.sh's one per 14 GB for scripts run by hand; GUARD_SLOTS given wins.
# Exported: a script it starts sees the same slot count.
if [ -z "$_slots_given" ]; then
  GUARD_SLOTS=$(( $(_guard_total_gb) / 8 )); [ "$GUARD_SLOTS" -lt 1 ] && GUARD_SLOTS=1; [ "$GUARD_SLOTS" -gt 4 ] && GUARD_SLOTS=4
fi
export GUARD_SLOTS
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
case ${TEST_FRAME:-} in
  '' | earth | flat) ;;
  *) echo "[test] TEST_FRAME=$TEST_FRAME: earth or flat" >&2; exit 2 ;;
esac
[ -n "${TEST_FRAME:-}" ] && export TEST_FRAME && echo "[test] every Godot row in frame $TEST_FRAME"
mkdir -p "$OUT"
export TEST_RUN="$$.$RANDOM$RANDOM"   # every process of this run inherits it: what `stop` sweeps for
printf '%s\n%s\n' $$ "$TEST_RUN" > "$PIDFILE"
RESULTS=$OUT/results.tsv
[ "${TEST_RESUME:-}" = 1 ] && [ -f "$RESULTS" ] || printf 'time\tcheck\tverdict\tseconds\tlog\tpeak_mb\n' > "$RESULTS"

ROWS=()
LEDGER=()   # one CSV line per check: tier,check,result,seconds,log,note,peak_mb
FAILED=0
SERVER=
cleanup() {
  [ -n "$SERVER" ] && _guard_kill_tree "$SERVER"
  [ -n "${UNIT_PID:-}" ] && _guard_kill_tree "$UNIT_PID"
  guard_unlock
  [ -n "${RESV:-}" ] && rm -f "$RESV/$$".*
  [ "$(sed -n 1p "$PIDFILE" 2>/dev/null)" = $$ ] && rm -f "$PIDFILE"
}
trap cleanup EXIT

# one line of results.tsv, appended as it lands (from heavy jobs too: one short line, one write)
result_line() { # name verdict seconds log [peak MB]
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$(date '+%F %T')" "$1" "$2" "$3" "$4" "${5:-}" >> "$RESULTS"
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

csv() { printf '"%s"' "${1//\"/\"\"}"; }   # one quoted CSV field: checks hold commas
ledger() { # tier name verdict seconds log [note] [peak MB]
  LEDGER+=("$1,$(csv "$2"),$3,$4,$(csv "$5"),$(csv "${6:-}"),${7:-}")
}
# one table row: check, verdict, seconds, peak MB (blank when none was printed), log or note
row() { local p=${4:-}; [ "$p" = 0 ] && p=; printf '%-30s %-8s %6s %7s  %s' "$1" "$2" "$3" "$p" "$5"; }
# a row of the table and the ledger, without results.tsv (a job wrote that as it landed)
add_row() { # name verdict seconds log tier [peak] [note]
  ROWS+=("$(row "$1" "$2" "$3" "${6:-}" "$4${7:+  ($7)}")")
  ledger "$5" "$1" "$2" "$3" "$4" "${7:-}" "${6:-}"
  [ "$2" = PASS ] || FAILED=1
}
record() { # name verdict seconds log [tier] [peak]
  result_line "$1" "$2" "$3" "$4" "${6:-}"
  add_row "$1" "$2" "$3" "$4" "${5:-build}" "${6:-}"
  echo "[test] $1: $2 (${3}s${6:+, peak ${6} MB})"
}

# --- peak RAM per check (#1702) ----------------------------------------------------------------
# Every Godot of a test run prints its OS peak working set as it quits; guard_run adds them up per check in
# GUARD_PEAK_FILE (tools/lib/guard.sh). The sum is kept machine-wide, beside the heavy lock, so every worktree's
# next run of that check is admitted by what it really took instead of a flat TEST_RAM_GB / TEST_HEAVY_RAM_GB.
PEAKS=${TEST_PEAKS:-${TMPDIR:-${TEMP:-/tmp}}/unitsport-check-peaks.tsv}
# kept short: the last 3000 lines once it passes 6000 (a few full runs)
if [ -f "$PEAKS" ] && [ "$(wc -l < "$PEAKS")" -gt 6000 ]; then
  tail -n 3000 "$PEAKS" > "$PEAKS.$$" && mv -f "$PEAKS.$$" "$PEAKS"
fi
# one line per run of a check: peak MB (0: none printed), the check, its seconds
peak_of() { [ -f "$PEAKS" ] && awk -F'\t' -v c="$1" '$2 == c && $1 > 0 { m = $1 } END { print m }' "$PEAKS"; }
need_mb() { # check default_gb: MB a check needs free to start, its last peak and a quarter more, else the default
  local p; p=$(peak_of "$1")
  if [ -n "$p" ] && [ "$p" -gt 0 ] 2>/dev/null; then echo $(( p + p / 4 + 256 )); else echo $(( $2 * 1024 )); fi
}
note_peak() { # check file seconds: the file's peaks summed (MB, 0 for none), kept in $PEAKS with the seconds
  local s; s=$(guard_peak_sum "$2"); rm -f "$2"
  printf '%s\t%s\t%s\n' "$s" "$1" "${3:-}" >> "$PEAKS"
  echo "$s"
}
# Longest first (#1702): the order to start checks in, by their last seconds here (never run: TEST_UNKNOWN_SECONDS,
# 120), so a 10 min check never starts last and holds the run up alone. Prints the indexes 0..n-1.
longest_first() { # check...
  [ $# -eq 0 ] && return
  # the path through ENVIRON, never -v: -v reads backslash escapes and mangles C:\Users\... (every check unknown)
  printf '%s\n' "$@" | LF_PEAKS="$PEAKS" awk -F'\t' -v unknown="${TEST_UNKNOWN_SECONDS:-120}" '
    BEGIN { peaks = ENVIRON["LF_PEAKS"]; while ((getline l < peaks) > 0) { split(l, f, "\t"); if (f[3] != "") s[f[2]] = f[3] } }
    { t = ($0 in s) ? s[$0] : unknown; printf "%d\t%d\n", t, NR - 1 }' | sort -t$'\t' -k1,1nr -k2,2n | cut -f2
}

# --- which checks -----------------------------------------------------------------------------
changed_files() {
  { git diff --name-only "$BASE"...HEAD; git diff --name-only HEAD; git ls-files --others --exclude-standard; } 2>/dev/null | grep -v '.uid$' | sort -u
}
rpc_touched() {
  { git diff -U0 "$BASE"...HEAD -- '*.cs'; git diff -U0 HEAD -- '*.cs'; } 2>/dev/null |
    grep -Eq '^[+-].*(\[Rpc|Rpc(Id)?\(|ReplicationConfig|MultiplayerSynchronizer|MultiplayerSpawner)'
}

# the code that uses the coordinate system (#1429): what a coordinate or rendering change can reach
coord_files() {
  {
    grep -rlE '\bGlobalPos\b|WorldOrigin|OriginFrame|ToWorld\(|ToGlobal\(|ToLv95\(|TileAt\(|IOriginShiftAware|HorizonGround|horizon\.bin' src --include='*.cs'
    git ls-files shaders src/Styles
  } 2>/dev/null | grep -v '.uid$' | sort -u
}

case $TIER in
  unit) WANT="" ;;
  quick) WANT=" quick " ;;
  net) WANT=" quick net " ;;
  full | coords) WANT=" quick net full " ;;
esac

if [ "$TIER" = coords ]; then CHANGED=$(coord_files); AREA=; else CHANGED=$(changed_files); fi
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
      [ "$TIER" = coords ] && RPC=yes   # positions cross the wire in every RPC
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

if [ "${TEST_LIST:-}" = 1 ]; then
  printf '%s\n' ${CHECKS[@]+"${CHECKS[@]}"}
  echo "[test] ${#CHECKS[@]} checks"
  exit 0
fi

# --- tier 0 -----------------------------------------------------------------------------------
# a row TEST_RESUME skips: in the table as passed earlier, not again in results.tsv
resumed() { ROWS+=("$(row "$1" PASS 0 "" "(passed earlier, TEST_RESUME)")"); echo "[test] $1: passed earlier, skipped"; }
# The unit stage, its rows in $OUT/.unit.rows (name, verdict, seconds, log; "resumed" and the name for a skip) and
# results.tsv as each lands. With Godot checks to run it goes beside them, started once the game is built (#1702):
# 4-5 min of dotnet test no longer come before the first check. TEST_UNIT_BESIDE=0 keeps it first.
unit_one() { # name log args...: one dotnet test run
  local name=$1 log=$2 t0=$SECONDS v; shift 2
  if passed_before "$name"; then printf 'resumed\t%s\n' "$name" >> "$OUT/.unit.rows"; return; fi
  guard_run "$TIMEOUT" "$log" dotnet test "$@"
  v=$(verdict $? "$log")
  result_line "$name" "$v" $((SECONDS - t0)) "$log"
  printf '%s\t%s\t%s\t%s\n' "$name" "$v" $((SECONDS - t0)) "$log" >> "$OUT/.unit.rows"
  echo "[test] $name: $v ($((SECONDS - t0))s) $(grep -aE "^(Passed|Failed)!" "$log" | tail -1)"
}
unit_stage() {
  : > "$OUT/.unit.rows"
  # [Trait("Speed", "Slow")] tests run in the full tier only (#1407). None is left: the signal test region it was
  # for builds once per run in ~30 s since RoadGen is optimized in Debug too (#1716)
  local slow=(--filter "Speed!=Slow"); [ "$TIER" = full ] && slow=()
  unit_one "unit (dotnet test)" "$OUT/unit.log" tests/UnitSportSwitzerland.Tests ${slow[@]+"${slow[@]}"}
  # RideLink (#901, tools/RideLink): the trainer library's own tests (parsers, the FTMS handshake, the router)
  unit_one "unit RideLink (dotnet test)" "$OUT/ridelink.log" tools/RideLink/tests/RideLink.Tests
}
unit_rows() { # the unit stage's rows into the table, first
  local a b c d before=("${ROWS[@]+"${ROWS[@]}"}")
  ROWS=()
  while IFS=$'\t' read -r a b c d; do
    if [ "$a" = resumed ]; then resumed "$b"; else add_row "$a" "$b" "$c" "$d" unit; fi
  done < "$OUT/.unit.rows"
  ROWS+=("${before[@]+"${before[@]}"}")
  rm -f "$OUT/.unit.rows"
}
UNIT_PID=
if [ ${#CHECKS[@]} -eq 0 ] || [ "${TEST_UNIT_BESIDE:-1}" = 0 ]; then unit_stage; unit_rows; fi

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
  guard_frame_args   # TEST_FRAME (#1683)
  ${UD[@]+"${UD[@]}"} "$GODOT" --headless ${TC_MAX_FPS:+--max-fps $TC_MAX_FPS} --path . -- --server --port "$port" --world fixture ${FRAME_ARGS[@]+"${FRAME_ARGS[@]}"} > "$slog" 2>&1 < /dev/null &
  SERVER=$!
  for _ in $(seq 1 600); do
    grep -q "server listening" "$slog" 2>/dev/null && break
    kill -0 "$SERVER" 2>/dev/null || break
    guard_nap 0.2
  done
  guard_run "$TIMEOUT" "$1" ${UD[@]+"${UD[@]}"} "$GODOT" --headless ${TC_MAX_FPS:+--max-fps $TC_MAX_FPS} --path . -- --leavecheck connect "127.0.0.1:$port" --world fixture ${FRAME_ARGS[@]+"${FRAME_ARGS[@]}"}
  local code=$?
  guard_frame_check netsmoke "$slog" "$1" >> "$1" || code=1
  _guard_kill_tree "$SERVER"
  wait "$SERVER" 2>/dev/null
  SERVER=
  return $code
}

# the whole check in the log's name: one flag runs in several worlds
# A long command line is cut to 120 characters plus a hash of the whole: Windows' 260-character paths
# refused the playtest smoke row's 330-character log name, and the check failed in 0 s with no log
log_of() {
  local name=${1//[^A-Za-z0-9]/_}; name=${name#"${name%%[!_]*}"}
  [ ${#name} -gt 120 ] && name="${name:0:120}_$(printf %s "$1" | cksum | cut -d' ' -f1)"
  echo "$OUT/$name.log"
}

# TEST_FRAME (#1710): a direct Godot row's frame arguments and the frame its log must state. A row with its own --frame
# keeps it and is held to it (two would be read differently: GameSettings takes the last, ServerWorld the first): the
# earth-only rows stay earth in a flat run. A row's own --frame flat in an earth run fails: every row plays earth there
row_frame() { # check: sets FRAME_ARGS and ROW_FRAME
  guard_frame_args
  ROW_FRAME=${TEST_FRAME:-}
  if [[ " $1 " =~ \ --frame\ ([a-z]+)\  ]]; then FRAME_ARGS=(); ROW_FRAME=${BASH_REMATCH[1]}; fi
  return 0
}
row_frame_check() { # check log: the log states the row's frame; a flat row in an earth run fails
  local name code=0
  name=$(row_name "$1")
  [ -z "${TEST_FRAME:-}" ] && return 0
  if [ "$TEST_FRAME" = earth ] && [ "$ROW_FRAME" = flat ]; then
    echo "[$name] RESULT: FAILED (the row plays --frame flat in a TEST_FRAME=earth run)"; return 1
  fi
  local keep=$TEST_FRAME
  TEST_FRAME=$ROW_FRAME
  guard_frame_check "$name" "$2" || code=1
  TEST_FRAME=$keep
  return $code
}

run_one() { # tier check log: runs one check, its exit code
  local tier=$1 check=$2 log=$3 eng= code to=$TIMEOUT
  # "@timeout=<s> <check>": a row longer than TEST_TIMEOUT (600 s) says so (the long drive, the long flight, #1710)
  if [[ $check == @timeout=* ]]; then to=${check%% *}; to=${to#@timeout=}; check=${check#* }; fi
  # shellcheck disable=SC2086  # the map's check is split into words on purpose
  case $check in
    @netsmoke) netsmoke "$log" ;;
    # a script passes TEST_FRAME (exported) to its own Godots and checks their logs (twoclient.sh, guard_frame_check);
    # the script's own log can hold its Godots' peak lines: only its inner guard_runs count them (GUARD_PEAK_FILE)
    tools/*) _guard_no_peak=1; guard_run "$to" "$log" bash $check; local c=$?; _guard_no_peak=0; return $c ;;
    @realtime\ *) ud "$log"; row_frame "$check"
       guard_run "$to" "$log" ${UD[@]+"${UD[@]}"} "$GODOT" --headless --path . -- ${check#@realtime } ${FRAME_ARGS[@]+"${FRAME_ARGS[@]}"}
       code=$?; row_frame_check "$check" "$log" >> "$log" || code=1; return $code ;;
    *) if [ "$tier" = quick ]; then eng=$FAST; elif [ -n "${TC_MAX_FPS:-}" ]; then eng="--max-fps $TC_MAX_FPS"; fi
       ud "$log"; row_frame "$check"
       guard_run "$to" "$log" ${UD[@]+"${UD[@]}"} "$GODOT" --headless $eng --path . -- $check ${FRAME_ARGS[@]+"${FRAME_ARGS[@]}"}
       code=$?; row_frame_check "$check" "$log" >> "$log" || code=1; return $code ;;
  esac
}
row_name() { # the check's flag, for its RESULT line: the first word ending in "check", else the first word
  local w
  for w in ${1#@realtime }; do [[ $w == --*check ]] && { echo "${w#--}"; return; }; done
  w=${1#@realtime }; w=${w%% *}; echo "${w#--}"
}

# --- side by side, admitted by measured RAM (#1008, #1702) ---------------------------------------
# A check starts when the RAM it needs is free: its last measured peak and a quarter (need_mb), else TEST_RAM_GB
# (3, quick) or TEST_HEAVY_RAM_GB (6, net/full), over the watchdog's floor, counting the full need of every job
# started less than the grow time ago (TEST_QUICK_GROW 15 s, TEST_JOB_STAGGER 40 s): their Godots have not reached
# their size yet. Older jobs are in the free RAM already. A check that failed or hit the RAM floor beside others
# runs again alone at the end, as a serial run would; a pass alone says it failed beside others (#1081).
# TEST_RETRY_ALONE=0: again alone only after the RAM floor.
# Machine-wide: every job started leaves "start grow_s need_mb" in $RESV (named <runner pid>.<n>), and admit also
# counts the jobs other runners (other worktrees, other sessions) started within their grow time. Without it, two
# runners each saw the RAM free and admitted into it, and their checks stopped at the floor, even ones run alone.
RESV=${TEST_RESERVED_DIR:-${PEAKS%.tsv}-reserved}
mkdir -p "$RESV" 2> /dev/null
_resv_n=0
reserve() { # need_mb grow_s: a job of this runner just started
  _resv_n=$(( _resv_n + 1 ))
  printf '%s %s %s\n' "${EPOCHSECONDS:-$(date +%s)}" "$2" "$1" > "$RESV/$$.$_resv_n" 2> /dev/null || true
}
_foreign_into() { # FOREIGN: MB that other runners' jobs still growing will take; expired files deleted
  local f t g m now=${EPOCHSECONDS:-$(date +%s)}
  FOREIGN=0
  for f in "$RESV"/*; do
    [ -f "$f" ] || continue
    read -r t g m < "$f" 2> /dev/null || continue
    if [ $(( now - t )) -ge "${g:-0}" ] 2> /dev/null; then rm -f "$f"; continue; fi
    case ${f##*/} in "$$".*) continue ;; esac   # this runner's own: counted from its job list
    FOREIGN=$(( FOREIGN + ${m:-0} ))
  done
}
admit() { # need_mb reserved_mb: room for one more job now
  _guard_free_into; _foreign_into
  [ $(( GUARD_FREE - $2 - FOREIGN )) -ge $(( $1 + GUARD_MIN_FREE_MB )) ]
}
admit_first() { # need_mb: this runner has nothing running: wait only while other runners' jobs are still growing
  _foreign_into
  [ "$FOREIGN" = 0 ] || admit "$1" 0
}

# Quick checks (one headless Godot, no ports) up to TEST_QUICK_JOBS at once: by default 3/8 of the cores and one per
# 5 GB of RAM, 1 to 8 (16 threads and 32 GB: 6; measured on the Avatar area, 6 beat 4 by 16 % for 10 % more CPU time
# per check). Every row runs beside any other (two --collidecheck rows held 361 s in a row, and they write nothing);
# one that clashes shows as "passed alone, failed beside others" below. TEST_QUICK_SERIAL lists the flags whose rows
# must never run together (space separated). @netsmoke (ports) and the tools/ scripts run one at a time after.
_cores=$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)
QJOBS=$(( _cores * 3 / 8 )); _q=$(( $(_guard_total_gb) / 5 )); [ "$_q" -lt "$QJOBS" ] && QJOBS=$_q
[ "$QJOBS" -lt 1 ] && QJOBS=1; [ "$QJOBS" -gt 8 ] && QJOBS=8
QJOBS=${TEST_QUICK_JOBS:-$QJOBS}
quick_alone() { case $1 in @netsmoke | tools/*) return 0 ;; esac; return 1; }
quick_group() { # the flag for one listed in TEST_QUICK_SERIAL, else the row itself
  local c=${1#@realtime }
  case " ${TEST_QUICK_SERIAL:-} " in *" ${c%% *} "*) echo "${c%% *}" ;; *) echo "$c" ;; esac
}

quick_job() { # index check: one quick check, its row in $OUT/.qrow.<index>: verdict, seconds, peak, RAM-floor stop, note
  local i=$1 check=$2 log t0=$SECONDS v peak ram=0
  log=$(log_of "$check")
  export GUARD_PEAK_FILE="$OUT/.peak.q$i"; rm -f "$GUARD_PEAK_FILE"
  run_one quick "$check" "$log"
  v=$(verdict $? "$log")
  peak=$(note_peak "$check" "$GUARD_PEAK_FILE" $((SECONDS - t0)))
  grep -aq "RAM floor\|MB free (floor" "$log" && ram=1
  result_line "$check" "$v" $((SECONDS - t0)) "$log" "$peak"
  printf '%s\n%s\n%s\n%s\n\n' "$v" $((SECONDS - t0)) "$peak" "$ram" > "$OUT/.qrow.$i"
  echo "[test] $check: $v ($((SECONDS - t0))s, peak ${peak} MB$([ $ram = 1 ] && echo ", stopped at the RAM floor"))"
}

run_quick() {
  local n=${#QUICK[@]} i k p check need reserved running busy started order=() pending=() qgroup=() qneed=() pids=() groups=() starts=() needs=() all=() t0=$SECONDS again=()
  echo "[test] ${n} quick checks, up to $QJOBS at once, longest first"
  rm -f "$OUT"/.qrow.*
  for (( i = 0; i < n; i++ )); do
    qgroup[i]=$(quick_group "${QUICK[$i]}")
    qneed[i]=$(need_mb "${QUICK[$i]}" "${TEST_RAM_GB:-3}")
  done
  order=($(longest_first "${QUICK[@]}")); pending=("${order[@]}")
  # start the first pending check, in longest-first order, whose group is not running (quick_group) and that the RAM admits
  while [ ${#pending[@]} -gt 0 ]; do
    running=0; reserved=0; busy=" "; started=0
    for k in ${!pids[@]}; do
      if kill -0 "${pids[$k]}" 2>/dev/null; then
        running=$(( running + 1 )); busy="$busy${groups[$k]} "
        [ $(( SECONDS - starts[k] )) -lt "${TEST_QUICK_GROW:-15}" ] && reserved=$(( reserved + needs[k] ))
      else unset 'pids[k]' 'groups[k]' 'starts[k]' 'needs[k]'; fi
    done
    if [ "$running" -lt "$QJOBS" ]; then
      for p in ${!pending[@]}; do
        i=${pending[$p]}
        [[ $busy == *" ${qgroup[i]} "* ]] && continue
        if [ "$running" = 0 ]; then
          admit_first "${qneed[i]}" || break   # another runner's job still growing
          # alone, as before: wait for TEST_RAM_GB free, then go
          if ! guard_wait_ram "${TEST_RAM_GB:-3}"; then
            printf 'FAIL\n0\n0\n0\nnot enough RAM\n' > "$OUT/.qrow.$i"; unset 'pending[p]'; started=1; break
          fi
        elif ! admit "${qneed[i]}" "$reserved"; then break   # the longest waits for its RAM: no smaller one jumps it
        fi
        ( quick_job "$i" "${QUICK[$i]}" ) &
        pids+=($!); all+=($!); groups+=("${qgroup[i]}"); starts+=($SECONDS); needs+=("${qneed[i]}")
        reserve "${qneed[i]}" "${TEST_QUICK_GROW:-15}"
        unset 'pending[p]'; started=1; break
      done
    fi
    [ "$started" = 0 ] && guard_nap 0.5
  done
  [ ${#all[@]} -gt 0 ] && wait "${all[@]}" 2>/dev/null   # these jobs only: the unit stage may still be going
  if [ "$QJOBS" -gt 1 ]; then
    for (( i = 0; i < n; i++ )); do
      [ -f "$OUT/.qrow.$i" ] || continue
      [ "$(sed -n 4p "$OUT/.qrow.$i")" = 1 ] && { again+=("$i"); continue; }
      [ "${TEST_RETRY_ALONE:-1}" != 0 ] && [ "$(sed -n 1p "$OUT/.qrow.$i")" != PASS ] && [ -z "$(sed -n 5p "$OUT/.qrow.$i")" ] && again+=("$i")
    done
  fi
  for i in ${again[@]+"${again[@]}"}; do
    echo "[test] again, alone: ${QUICK[$i]}"
    reserve "${qneed[i]}" "${TEST_QUICK_GROW:-15}"
    ( quick_job "$i" "${QUICK[$i]}" )
    [ "$(sed -n 1p "$OUT/.qrow.$i")" = PASS ] && sed -i '5s|^$|passed alone, failed beside others|' "$OUT/.qrow.$i"
  done
  for (( i = 0; i < n; i++ )); do
    if [ -f "$OUT/.qrow.$i" ]; then
      add_row "${QUICK[$i]}" "$(sed -n 1p "$OUT/.qrow.$i")" "$(sed -n 2p "$OUT/.qrow.$i")" "$(log_of "${QUICK[$i]}")" quick \
        "$(sed -n 3p "$OUT/.qrow.$i")" "$(sed -n 5p "$OUT/.qrow.$i")"
    else add_row "${QUICK[$i]}" FAIL 0 "" quick "" "no result"; fi
  done
  rm -f "$OUT"/.qrow.*
  echo "[test] the quick checks took $((SECONDS - t0))s side by side (${#again[@]} run again alone)"
}

# The net and full tiers: up to TEST_JOBS at once (default: the heavy lock's slot count), each in its own slot, so its
# ports are moved by GUARD_SLOT * 1000 (tools/lib/twoclient.sh). Two scripts that write the same logs (one execs the
# other) never run together, and the load test and the trailer run alone. Beside others every headless Godot runs at
# TEST_MAX_FPS (60).
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

heavy_job() { # index tier check need_mb: one check in a slot of its own, its row in $OUT/.row.<index>
  local i=$1 tier=$2 check=$3 need=$4 log t0=$SECONDS res v= ram=0 peak=
  trap guard_unlock EXIT
  log=$(log_of "$check")
  if ! guard_lock; then res="$(row "$check" FAIL 0 "" "(lock)")"
  elif ! guard_wait_ram $(( (need + 1023) / 1024 )); then res="$(row "$check" FAIL 0 "" "(not enough RAM)")"
  else
    export GUARD_LOCK_HELD=1 GUARD_PEAK_FILE="$OUT/.peak.h$i"
    rm -f "$GUARD_PEAK_FILE"
    t0=$SECONDS
    run_one "$tier" "$check" "$log"
    v=$(verdict $? "$log")
    peak=$(note_peak "$check" "$GUARD_PEAK_FILE" $((SECONDS - t0)))
    # stopped by the RAM watchdog, not by what it checks: run again alone at the end
    grep -aq "RAM floor\|MB free (floor" "$log" && ram=1
    res="$(row "$check" "$v" $((SECONDS - t0)) "$peak" "$log")"
    result_line "$check" "$v" $((SECONDS - t0)) "$log" "$peak"
    echo "[test] $check: $v ($((SECONDS - t0))s, slot $GUARD_SLOT, peak ${peak} MB$([ $ram = 1 ] && echo ", stopped at the RAM floor"))"
  fi
  # verdict, table row, RAM-floor stop, the ledger's line without its note, the note, the peak
  printf '%s\n%s\n%s\n%s,%s,%s,%s,%s\n\n%s\n' "${v:-FAIL}" "$res" "$ram" \
    "$tier" "$(csv "$check")" "${v:-FAIL}" $((SECONDS - t0)) "$(csv "$log")" "$peak" > "$OUT/.row.$i"
}

run_heavy() {
  local i n=${#HEAVY[@]} pids=() groups=() starts=() needs=() all=() k busy running reserved entry tier check g need t0=$SECONDS again=()
  echo "[test] ${n} net/full checks, up to $JOBS at once, longest first"
  rm -f "$OUT"/.row.*
  local hchecks=() order
  for entry in "${HEAVY[@]}"; do hchecks+=("${entry#* }"); done
  order=$(longest_first "${hchecks[@]}")
  for i in $order; do
    entry=${HEAVY[$i]}; tier=${entry%% *}; check=${entry#* }; g=$(group_of "$check"); need=$(need_mb "$check" "${TEST_HEAVY_RAM_GB:-6}")
    # wait for a free job and room for it, for the job of the same group, and for every job before one that runs alone
    while :; do
      busy=0; running=0; reserved=0
      for k in ${!pids[@]}; do
        if kill -0 "${pids[$k]}" 2>/dev/null; then
          running=$(( running + 1 ))
          if alone "$check" || [ "${groups[$k]}" = "$g" ] || alone "${groups[$k]}"; then busy=$JOBS; fi
          busy=$(( busy + 1 ))
          [ $(( SECONDS - starts[k] )) -lt "${TEST_JOB_STAGGER:-40}" ] && reserved=$(( reserved + needs[k] ))
        else unset 'pids[k]' 'groups[k]' 'starts[k]' 'needs[k]'; fi
      done
      [ "$busy" -lt "$JOBS" ] && { if [ "$running" = 0 ]; then admit_first "$need"; else admit "$need" "$reserved"; fi; } && break
      guard_nap 1
    done
    ( export TC_MAX_FPS=${TEST_MAX_FPS:-60}; heavy_job "$i" "$tier" "$check" "$need" ) &
    pids+=($!); all+=($!); groups+=("$(alone "$check" && echo "$check" || echo "$g")"); starts+=($SECONDS); needs+=("$need")
    reserve "$need" "${TEST_JOB_STAGGER:-40}"
  done
  [ ${#all[@]} -gt 0 ] && wait "${all[@]}" 2>/dev/null
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
    reserve "$(need_mb "${entry#* }" "${TEST_HEAVY_RAM_GB:-6}")" "${TEST_JOB_STAGGER:-40}"
    ( heavy_job "$i" "${entry%% *}" "${entry#* }" "$(need_mb "${entry#* }" "${TEST_HEAVY_RAM_GB:-6}")" )   # a subshell: its EXIT trap must not replace the runner's
    [ "$why" = "failed beside the others" ] && [ "$(sed -n 1p "$OUT/.row.$i")" = PASS ] &&
      sed -i -e '2s|$|  (passed alone, failed beside others)|' -e '5s|^$|passed alone, failed beside others|' "$OUT/.row.$i"
  done
  for (( i = 0; i < n; i++ )); do
    if [ -f "$OUT/.row.$i" ]; then
      ROWS+=("$(sed -n 2p "$OUT/.row.$i")"); [ "$(sed -n 1p "$OUT/.row.$i")" = PASS ] || FAILED=1
      LEDGER+=("$(sed -n 4p "$OUT/.row.$i"),$(csv "$(sed -n 5p "$OUT/.row.$i")"),$(sed -n 6p "$OUT/.row.$i")")
    else
      ROWS+=("$(row "${HEAVY[$i]#* }" FAIL 0 "" "(no result)")"); FAILED=1
      ledger "${HEAVY[$i]%% *}" "${HEAVY[$i]#* }" FAIL 0 "" "no result"
    fi
  done
  rm -f "$OUT"/.row.*
  echo "[test] the net/full checks took $((SECONDS - t0))s side by side (${#again[@]} run again alone)"
}

T_GODOT=$SECONDS
if [ ${#CHECKS[@]} -gt 0 ]; then
  if ! command -v "$GODOT" > /dev/null; then
    echo "[test] no Godot at '$GODOT': set GODOT (docs/notes/general/godot-exe.md)" >&2
    record "godot" FAIL 0 "-"
    [ "${TEST_UNIT_BESIDE:-1}" != 0 ] && { unit_stage; unit_rows; }
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
    # the unit stage beside the Godot checks, from here: the game's build is done, so the two builds never meet
    [ "${TEST_UNIT_BESIDE:-1}" != 0 ] && { unit_stage & UNIT_PID=$!; }
    if [ $code = 0 ]; then
      locked=0
      QUICK=(); SERIAL=(); HEAVY=()
      for entry in "${CHECKS[@]}"; do
        tier=${entry%% *}
        check=${entry#* }
        passed_before "$check" && { resumed "$check"; continue; }
        if [ "$tier" = quick ]; then
          if quick_alone "$check" || [ "$QJOBS" -le 1 ]; then SERIAL+=("$entry"); else QUICK+=("$check"); fi
        elif [ "$JOBS" -gt 1 ]; then HEAVY+=("$entry")   # side by side below
        else SERIAL+=("$entry"); fi
      done
      [ ${#QUICK[@]} -gt 0 ] && run_quick
      for entry in ${SERIAL[@]+"${SERIAL[@]}"}; do
        tier=${entry%% *}
        check=${entry#* }
        log=$(log_of "$check")
        need=$(need_mb "$check" "${TEST_RAM_GB:-3}")
        if [ "$tier" = quick ]; then
          guard_wait_ram $(( (need + 1023) / 1024 )) || { record "$check" FAIL 0 "(not enough RAM)"; continue; }
        else
          # tier 2/3, TEST_JOBS=1: one at a time, in one slot held for the whole run
          need=$(need_mb "$check" "${TEST_HEAVY_RAM_GB:-6}")
          [ $locked = 1 ] || { guard_lock && locked=1 && export GUARD_LOCK_HELD=1; } || { record "$check" FAIL 0 "(lock)"; continue; }
          guard_wait_ram $(( (need + 1023) / 1024 )) || { record "$check" FAIL 0 "(not enough RAM)"; continue; }
        fi
        t0=$SECONDS
        export GUARD_PEAK_FILE="$OUT/.peak.serial"; rm -f "$GUARD_PEAK_FILE"
        run_one "$tier" "$check" "$log"
        code=$?
        record "$check" "$(verdict $code "$log")" $((SECONDS - t0)) "$log" "$tier" "$(note_peak "$check" "$GUARD_PEAK_FILE" $((SECONDS - t0)))"
      done
      unset GUARD_PEAK_FILE
      [ ${#HEAVY[@]} -gt 0 ] && run_heavy
    fi
    if [ -n "$UNIT_PID" ]; then
      kill -0 "$UNIT_PID" 2>/dev/null && echo "[test] waiting for the unit stage"
      wait "$UNIT_PID"; UNIT_PID=
      unit_rows
    fi
  fi
elif [ "$TIER" != unit ]; then
  echo "[test] no Godot check matches ${AREA:-the changed files}"
fi
[ ${#CHECKS[@]} -gt 0 ] && echo "[test] the Godot part took $((SECONDS - T_GODOT))s"

echo
printf '%-30s %-8s %6s %7s  %s\n' CHECK RESULT SECONDS PEAK_MB LOG
printf '%s\n' "${ROWS[@]}"

# the results as CSV, to keep beside a baseline and compare with tools/test-compare.py (#1429)
LEDGER_FILE=${TEST_LEDGER:-$OUT/results.csv}
mkdir -p "$(dirname "$LEDGER_FILE")"
{
  echo "# tools/test.sh $TIER ${AREA} commit $(git rev-parse --short HEAD 2>/dev/null) $(date -u +%Y-%m-%dT%H:%MZ)"
  echo "tier,check,result,seconds,log,note,peak_mb"
  printf '%s\n' ${LEDGER[@]+"${LEDGER[@]}"}
} > "$LEDGER_FILE"
echo "[test] results: $LEDGER_FILE"
exit $FAILED
