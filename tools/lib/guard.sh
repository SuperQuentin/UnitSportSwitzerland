# Resource guard for heavy test runs (docs/notes/general/testing.md). Source it, then:
#   guard_wait_ram <GB> [max_wait_s]   wait until that much RAM is free (fails after max_wait_s)
#   guard_lock [max_wait_s] [max_hold_s]  take a slot of the machine-wide heavy-run lock (GUARD_SLOTS of them,
#                                      #1008), queueing when all are held; sets GUARD_SLOT (0..);
#                                      max_hold_s (default 3600) is how long you may keep it
#   guard_unlock                       release it (put it in your EXIT trap)
#   guard_status                       who holds the lock, since when, last heartbeat
# A lock is taken over as stale, without waiting, when its owner's PID is gone, when its heartbeat
# (touched every GUARD_BEAT s by a helper that dies with the owner) is older than GUARD_STALE s,
# or when it has been held longer than the owner's max_hold_s + 120 s.
#   guard_run <timeout_s> <log> cmd... run cmd with its output in log; kill its process tree,
#                                      and only that, if it overruns (124) or if free RAM falls under
#                                      GUARD_MIN_FREE_MB, or if cmd's own memory passes GUARD_MAX_MB
#                                      (137). Otherwise returns cmd's exit code.
#   guard_test_userdata <dir>          the Godots started after it get a fresh, quiet user:// in <dir> (#1378):
#                                      APPDATA / XDG_DATA_HOME exported; on macOS prefix the Godot run with
#                                      ${GUARD_GODOT_ENV[@]+"${GUARD_GODOT_ENV[@]}"} (HOME, never exported)
#   guard_quiet_godot <root>           writes test_window.cfg in the user:// that <root> gives as APPDATA /
#                                      XDG_DATA_HOME / HOME: project.godot's settings override, read before
#                                      the window exists, so a test window never takes focus and is silent
#   guard_docker_up                    Docker answers within 15 s; else false, the reason in GUARD_DOCKER_WHY
#   guard_watch <pid> [min_free_mb]    background RAM watchdog for a process you started yourself
#                                      (not through guard_run): kills that PID's tree, and only it,
#                                      when free RAM falls under the floor. Prints the watchdog's PID.
#                                      `guard_watch $$` is safe: the script's children go, then the
#                                      script gets a TERM (exit 143, traps run), never a silent exit 0.
# RAM watchdog: free RAM is read every GUARD_MEM_EVERY (2) s from /proc/meminfo (Git Bash maps it
# to Windows' free physical memory, ~30 ms); under GUARD_MIN_FREE_MB (1500) the run is killed
# before Windows or WSL run out of memory and take everything else down with them.
# Memory cap: every GUARD_MAX_EVERY (10) s guard_run sums its run's memory (macOS: the footprint, compressed
# and swapped pages too; Linux: resident) and kills it over GUARD_MAX_MB (10240). On macOS free pages hide a
# leak: compression and swap kept them above the floor while a leaking client reached 128 GB (#837).
# Works in Git Bash on Windows, on Linux and on macOS (vm_stat). Never kills by name: only the PIDs it started.

GUARD_LOCK_DIR=${GUARD_LOCK_DIR:-${TMPDIR:-${TEMP:-/tmp}}/unitsport-heavy.lock}
# Slots (#1008): the heavy lock is GUARD_SLOTS directories, $GUARD_LOCK_DIR (slot 0) then $GUARD_LOCK_DIR.1,
# .2 ...; guard_lock takes any free one and sets GUARD_SLOT. Default: one per 14 GB of RAM, 1 to 4 (32 GB: 2).
# GUARD_SLOTS=1 is the old single lock, and a script from before #1008 only ever waits for slot 0.
# (One per 10 GB gave 3 on 32 GB: real-terrain two-client checks ran it to the RAM floor 6 times in one
# full run, each job lost and run again alone, ~15 min; #1407.)
_guard_lock_base=${_guard_lock_base:-$GUARD_LOCK_DIR}
_guard_total_gb() {
  if [ -r /proc/meminfo ]; then awk '/^MemTotal:/ { print int($2 / 1048576) }' /proc/meminfo
  elif [ "$(uname)" = Darwin ]; then echo $(( $(sysctl -n hw.memsize 2>/dev/null || echo 8589934592) / 1073741824 ))
  else echo 8; fi
}
if [ -z "${GUARD_SLOTS:-}" ]; then
  GUARD_SLOTS=$(( $(_guard_total_gb) / 14 ))
  [ "$GUARD_SLOTS" -lt 1 ] && GUARD_SLOTS=1
  [ "$GUARD_SLOTS" -gt 4 ] && GUARD_SLOTS=4
fi
GUARD_SLOT=${GUARD_SLOT:-0}
_guard_slot_dir() { if [ "$1" = 0 ]; then echo "$_guard_lock_base"; else echo "$_guard_lock_base.$1"; fi; }
GUARD_POLL=${GUARD_POLL:-10}
GUARD_BEAT=${GUARD_BEAT:-10}
GUARD_STALE=${GUARD_STALE:-45}
GUARD_MIN_FREE_MB=${GUARD_MIN_FREE_MB:-1500}
GUARD_MEM_EVERY=${GUARD_MEM_EVERY:-2}
GUARD_MAX_MB=${GUARD_MAX_MB:-10240}
GUARD_MAX_EVERY=${GUARD_MAX_EVERY:-10}
_guard_have_lock=0
_guard_beat_pid=

_guard_windows() { [ -n "${WINDIR:-}" ] || [ -n "${windir:-}" ]; }

# A test's Godot window (#1378): project.godot points application/config/project_settings_override at
# user://test_window.cfg, which only a test's own user:// holds. no_focus: the window is made without
# taking focus and never takes it later; the Dummy driver still mixes (as headless), nothing is heard;
# unitsport/test_window: the game never captures the pointer (MouseCapture.TestWindow), whatever the probe.
guard_quiet_godot() {
  local ud
  case "$(uname -s)" in
    Darwin) ud="$1/Library/Application Support/Godot/app_userdata/UnitSportSwitzerland" ;;
    MINGW*|MSYS*|CYGWIN*) ud="$1/Godot/app_userdata/UnitSportSwitzerland" ;;
    *) ud="$1/godot/app_userdata/UnitSportSwitzerland" ;;
  esac
  mkdir -p "$ud"
  printf '[display]\n\nwindow/size/no_focus=true\n\n[audio]\n\ndriver/driver="Dummy"\n\n[unitsport]\n\ntest_window=true\n' > "$ud/test_window.cfg"
}

# Docker answering within 15 s (#1407): with Docker Desktop's engine down `docker info` hangs instead of
# failing, and a Nakama check waited out its 10 min timeout. False with the reason in GUARD_DOCKER_WHY.
GUARD_DOCKER_WHY=
guard_docker_up() {
  command -v docker > /dev/null || { GUARD_DOCKER_WHY="no docker on PATH"; return 1; }
  local t=()
  command -v timeout > /dev/null && t=(timeout 15)
  "${t[@]+"${t[@]}"}" docker info > /dev/null 2>&1 && return 0
  local code=$?
  if [ "$code" = 124 ]; then GUARD_DOCKER_WHY="Docker did not answer in 15 s: restart Docker Desktop"
  else GUARD_DOCKER_WHY="no Docker engine: start Docker Desktop"; fi
  return 1
}

GUARD_GODOT_ENV=()
guard_test_userdata() {
  local d
  mkdir -p "$1" && d=$(cd "$1" && pwd) || return 1
  find "$d" -mindepth 1 -delete
  if _guard_windows; then export APPDATA="$(cygpath -w "$d")"
  elif [ "$(uname)" = Darwin ]; then GUARD_GODOT_ENV=(env HOME="$d")
  else export XDG_DATA_HOME="$d"; fi
  guard_quiet_godot "$d"
}

# free RAM in whole MB into GUARD_FREE, starting no process where /proc/meminfo is there (#1409): the
# watchdogs poll it every 2 s each, and $(guard_free_mb) cost a subshell and an awk every time; a full
# run's dozen watchdogs then started ~30 processes a second, and Git Bash ran out ("fork: retry")
_guard_free_into() {
  if [ -r /proc/meminfo ]; then
    local key kb avail= free=
    while read -r key kb _; do
      case $key in
        MemAvailable:) avail=$kb ;;
        MemFree:) free=$kb ;;
      esac
    done < /proc/meminfo
    GUARD_FREE=$(( ${avail:-${free:-0}} / 1024 ))
  else
    GUARD_FREE=$(guard_free_mb)
  fi
}

# free RAM in whole MB
guard_free_mb() {
  # Git Bash and Linux both have /proc/meminfo (Git Bash: MemFree = Windows' free physical memory)
  if [ -r /proc/meminfo ]; then
    awk '/^MemAvailable:/ { a = $2 } /^MemFree:/ { f = $2 } END { print int((a ? a : f) / 1024) }' /proc/meminfo
  elif [ "$(uname)" = Darwin ]; then
    # macOS: free, inactive and purgeable pages are what can be had without swapping
    vm_stat | awk '/page size of/ { size = $8 } /^Pages (free|inactive|purgeable):/ { gsub(/\./, "", $NF); pages += $NF }
      END { print int(pages * size / 1048576) }'
  else
    local kb
    kb=$(powershell -NoProfile -c "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory" | tr -dc '0-9')
    echo $(( kb / 1024 ))
  fi
}

guard_wait_ram() {
  local need_mb=$(( $1 * 1024 )) max=${2:-1800} waited=0 free
  while :; do
    _guard_free_into; free=$GUARD_FREE
    [ "$free" -ge "$need_mb" ] && return 0
    if [ "$waited" -ge "$max" ]; then
      echo "[guard] only ${free} MB free after ${waited}s, need $1 GB: giving up" >&2
      return 1
    fi
    [ "$waited" -eq 0 ] && echo "[guard] ${free} MB free, waiting for $1 GB" >&2
    sleep "$GUARD_POLL"; waited=$(( waited + GUARD_POLL ))
  done
}

_guard_lock_pid() { cat "$GUARD_LOCK_DIR/pid" 2>/dev/null; }
_guard_field() { sed -n "s/^$1=//p" "$GUARD_LOCK_DIR/info" 2>/dev/null; }
_guard_mtime() { stat -c %Y "$1" 2>/dev/null || stat -f %m "$1" 2>/dev/null || echo 0; }   # GNU, then BSD (macOS)

_guard_drop_lock() {
  rm -f "$GUARD_LOCK_DIR/pid" "$GUARD_LOCK_DIR/info" "$GUARD_LOCK_DIR/beat"
  rmdir "$GUARD_LOCK_DIR" 2>/dev/null
}

# why the current lock is stale, or nothing if it is live
_guard_stale_reason() {
  local pid now beat started hold
  pid=$(_guard_lock_pid); now=$(date +%s)
  if [ -z "$pid" ]; then
    # the pid file is written right after mkdir; 60 s, not less: a hand-written lock loop or a busy
    # machine can be slow to write it, and taking a live lock over runs two heavy jobs at once
    [ $(( now - $(_guard_mtime "$GUARD_LOCK_DIR") )) -ge 60 ] && echo "no owner recorded for 60 s"
    return
  fi
  if ! kill -0 "$pid" 2>/dev/null; then echo "owner PID $pid is gone"; return; fi
  beat=$(_guard_mtime "$GUARD_LOCK_DIR/beat")
  # an old-style lock without a heartbeat file is judged on the PID alone
  if [ -e "$GUARD_LOCK_DIR/beat" ] && [ $(( now - beat )) -gt "$GUARD_STALE" ]; then
    echo "no heartbeat for $(( now - beat ))s (owner PID $pid reused or hung)"; return
  fi
  started=$(_guard_field started); hold=$(_guard_field max_hold)
  if [ -n "$started" ] && [ -n "$hold" ] && [ $(( now - started )) -gt $(( hold + 120 )) ]; then
    echo "held $(( now - started ))s, over its max of ${hold}s"; return
  fi
}

guard_status() {
  local keep=$GUARD_LOCK_DIR s now held=0 why
  now=$(date +%s)
  for (( s = 0; s < GUARD_SLOTS; s++ )); do
    GUARD_LOCK_DIR=$(_guard_slot_dir "$s")
    [ -d "$GUARD_LOCK_DIR" ] || continue
    held=$(( held + 1 ))
    echo "[guard] slot $s/$GUARD_SLOTS held by PID $(_guard_lock_pid) ($(_guard_field what)) for $(( now - $(_guard_field started 2>/dev/null || echo "$now") ))s," \
      "heartbeat $(( now - $(_guard_mtime "$GUARD_LOCK_DIR/beat") ))s ago"
    why=$(_guard_stale_reason); [ -n "$why" ] && echo "[guard] STALE: $why"
  done
  GUARD_LOCK_DIR=$keep
  [ "$held" = 0 ] && echo "[guard] lock free ($GUARD_SLOTS slots)"
  return 0
}

# takes slot dir $GUARD_LOCK_DIR if it is free, or stale (then taken over); true on success
_guard_take_slot() {
  mkdir "$GUARD_LOCK_DIR" 2>/dev/null && return 0
  local why pid
  why=$(_guard_stale_reason)
  [ -n "$why" ] || return 1
  pid=$(_guard_lock_pid)
  # two waiters can both judge it stale; re-reading the pid first makes the race tiny, not zero
  if [ "$(_guard_lock_pid)" = "$pid" ]; then
    echo "[guard] removing stale lock (slot $s): $why" >&2
    _guard_drop_lock
  fi
  mkdir "$GUARD_LOCK_DIR" 2>/dev/null
}

# this shell's PID, a subshell's too: BASHPID, or on bash 3.2 (macOS, no BASHPID) the parent of a
# child it starts. Call it as a plain command, never in $(...): that would be another subshell.
_guard_self_pid() { if [ -n "${BASHPID:-}" ]; then echo "$BASHPID"; else sh -c 'echo $PPID'; fi; }
_guard_my_pid=

guard_lock() {
  local max=${1:-3600} hold=${2:-3600} waited=0 s
  while :; do
    # the first free slot, slot 0 first: a lone run keeps today's lock and ports
    for (( s = 0; s < GUARD_SLOTS; s++ )); do
      GUARD_LOCK_DIR=$(_guard_slot_dir "$s")
      _guard_take_slot && break 2
    done
    GUARD_LOCK_DIR=$_guard_lock_base
    if [ "$waited" -ge "$max" ]; then
      echo "[guard] all $GUARD_SLOTS heavy-run slots still held after ${waited}s: $(guard_status | head -1)" >&2
      return 1
    fi
    [ $(( waited % 120 )) -eq 0 ] && guard_status >&2
    sleep 2; waited=$(( waited + 2 ))
  done
  GUARD_SLOT=$s
  export GUARD_SLOT
  _guard_self_pid > "$GUARD_LOCK_DIR/pid"
  _guard_my_pid=$(_guard_lock_pid)
  printf 'started=%s
max_hold=%s
what=%s
' "$(date +%s)" "$hold" "${GUARD_WHAT:-$0 $*}" > "$GUARD_LOCK_DIR/info"
  touch "$GUARD_LOCK_DIR/beat"
  # heartbeat: dies with its owner, so a dead or PID-reused owner goes stale in GUARD_STALE s
  local owner=$_guard_my_pid
  ( while kill -0 "$owner" 2>/dev/null && [ -d "$GUARD_LOCK_DIR" ]; do touch "$GUARD_LOCK_DIR/beat" 2>/dev/null; sleep "$GUARD_BEAT"; done ) &
  _guard_beat_pid=$!
  _guard_have_lock=1
}

guard_unlock() {
  [ "$_guard_have_lock" = 1 ] || return 0
  [ -n "$_guard_beat_pid" ] && kill "$_guard_beat_pid" 2>/dev/null
  _guard_beat_pid=
  [ "$(_guard_lock_pid)" = "$_guard_my_pid" ] && _guard_drop_lock
  _guard_have_lock=0
}

# a PID and all its descendants as Git Bash / Linux sees them (ps -ef pid/ppid), the PID first
_guard_tree() {
  ps -ef 2>/dev/null | awk -v root="$1" 'NR > 1 { kids[$3] = kids[$3] " " $2 }
    END { q = root; while (q != "") { n = split(q, a, " "); q = ""; for (i = 1; i <= n; i++) { print a[i]; q = q kids[a[i]] } } }'
}

# one run's memory in whole MB, its whole tree: macOS's footprint (top's MEM: compressed and swapped pages
# too), Linux's resident size; 0 on Windows (not measured there)
_guard_mem_mb() {
  _guard_windows && { echo 0; return; }
  local p m total=0
  for p in $(_guard_tree "$1"); do
    if [ "$(uname)" = Darwin ]; then
      m=$(top -l 1 -pid "$p" -stats mem 2>/dev/null | tail -1 | tr -d ' +-' | awk '/G$/ { print int($0 * 1024); next } /M$/ { print int($0); next } { print 0 }')
    else
      m=$(( $(ps -o rss= -p "$p" 2>/dev/null | tr -dc '0-9' || echo 0) / 1024 ))
    fi
    total=$(( total + ${m:-0} ))
  done
  echo "$total"
}

# kill one PID we started, with everything it started
_guard_kill_tree() {
  local pid=$1 p
  if _guard_windows; then
    # taskkill /T follows Windows parentage only: a process Git Bash forked is not the Windows child
    # of its bash, so walk the Git Bash tree too; /T then takes each one's native children (the
    # real Godot under its _console.exe wrapper)
    for p in $(_guard_tree "$pid"); do
      [ -r "/proc/$p/winpid" ] && taskkill //F //T //PID "$(cat "/proc/$p/winpid")" > /dev/null 2>&1
      kill -KILL "$p" 2>/dev/null
    done
  else
    # the tree first: once its root is dead, its children belong to init and cannot be found (macOS
    # has no setsid, so there is no process group to kill instead)
    local tree; tree=$(_guard_tree "$pid")
    kill -TERM -- "-$pid" 2>/dev/null || kill -TERM $tree 2>/dev/null
    sleep 2
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
    for p in $tree $(_guard_tree "$pid"); do kill -KILL "$p" 2>/dev/null; done
  fi
}

# guard_nap <seconds>: the polls' sleep (#992). A FIFO read instead of the fork crashed Git Bash ("fifo_reader:
# fatal error - Can't add a client handler"), so it is plain sleep; guard_run no longer polls at all.
guard_nap() { sleep "$1"; }

guard_run() {
  local timeout_s=$1 log=$2 pid wd code why
  shift 2
  mkdir -p "$(dirname "$log")"
  # its own process group on Linux, so the timeout can take its children down with it
  if command -v setsid > /dev/null && ! _guard_windows; then
    setsid "$@" > "$log" 2>&1 < /dev/null &
  else
    "$@" > "$log" 2>&1 < /dev/null &
  fi
  pid=$!
  why="${TMPDIR:-${TEMP:-/tmp}}/guard_run.$$.$pid"
  rm -f "$why"
  # the watchdog ticks once a second beside the run and writes its exit code before it kills it; the run's end
  # is seen at once by the wait below, not on a poll (#992)
  (
    elapsed=0
    while kill -0 "$pid" 2>/dev/null; do
      if [ "$elapsed" -ge "$timeout_s" ]; then
        echo "[guard] timeout after ${timeout_s}s, killing PID $pid and its children" >> "$log"
        echo 124 > "$why"; _guard_kill_tree "$pid"; exit 0
      fi
      if [ $(( elapsed % GUARD_MEM_EVERY )) -eq 0 ]; then
        _guard_free_into; free=$GUARD_FREE
        if [ "$free" -lt "$GUARD_MIN_FREE_MB" ]; then
          echo "[guard] only ${free} MB free (floor ${GUARD_MIN_FREE_MB} MB): killing PID $pid and its children" | tee -a "$log" >&2
          echo 137 > "$why"; _guard_kill_tree "$pid"; exit 0
        fi
      fi
      if [ "$elapsed" -gt 0 ] && [ $(( elapsed % GUARD_MAX_EVERY )) -eq 0 ]; then
        used=$(_guard_mem_mb "$pid")
        if [ "${used:-0}" -gt "$GUARD_MAX_MB" ]; then
          echo "[guard] PID $pid uses ${used} MB (cap GUARD_MAX_MB ${GUARD_MAX_MB} MB): killing it and its children" | tee -a "$log" >&2
          echo 137 > "$why"; _guard_kill_tree "$pid"; exit 0
        fi
      fi
      sleep 1; elapsed=$(( elapsed + 1 ))
    done
  ) &
  wd=$!
  wait "$pid"; code=$?
  kill "$wd" 2>/dev/null; wait "$wd" 2>/dev/null
  [ -s "$why" ] && code=$(cat "$why")
  rm -f "$why"
  return "$code"
}

guard_watch() {
  local target=$1 floor=${2:-$GUARD_MIN_FREE_MB}
  (
    local me c
    me=$(exec sh -c 'echo $PPID')   # this watchdog's own PID (no BASHPID in bash 3.2)
    while kill -0 "$target" 2>/dev/null; do
      _guard_free_into; free=$GUARD_FREE
      if [ "$free" -lt "$floor" ]; then
        echo "[guard] only ${free} MB free (floor ${floor} MB): killing PID $target and its children" >&2
        # a TERM, then its children, never this watchdog (target may be the script that started it, $$):
        # a script dies 143 with its traps run, not killed outright with exit 0 and no RESULT line. Bash
        # holds a trapped TERM until its foreground child ends, so the TERM goes first (else the script
        # would run on past a killed child to its own verdict), and again each second for a child started
        # since; killed outright only as a last resort
        for _ in 1 2 3 4 5 6 7 8 9 10; do
          kill -TERM "$target" 2>/dev/null
          for c in $(ps -ef 2>/dev/null | awk -v p="$target" -v me="$me" 'NR > 1 && $3 == p && $2 != me { print $2 }'); do
            _guard_kill_tree "$c"
          done
          sleep 1
          kill -0 "$target" 2>/dev/null || exit 0
        done
        _guard_kill_tree "$target"
        exit 0
      fi
      sleep "$GUARD_MEM_EVERY"
    done
  ) > /dev/null &   # never hold the caller's $(...) pipe open, or `w=$(guard_watch ...)` hangs
  echo $!
}
