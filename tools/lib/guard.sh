# Resource guard for heavy test runs (docs/notes/general/testing.md). Source it, then:
#   guard_wait_ram <GB> [max_wait_s]   wait until that much RAM is free (fails after max_wait_s)
#   guard_lock [max_wait_s] [max_hold_s]  take the machine-wide heavy-run lock, queueing behind others;
#                                      max_hold_s (default 3600) is how long you may keep it
#   guard_unlock                       release it (put it in your EXIT trap)
#   guard_status                       who holds the lock, since when, last heartbeat
# A lock is taken over as stale, without waiting, when its owner's PID is gone, when its heartbeat
# (touched every GUARD_BEAT s by a helper that dies with the owner) is older than GUARD_STALE s,
# or when it has been held longer than the owner's max_hold_s + 120 s.
#   guard_run <timeout_s> <log> cmd... run cmd with its output in log; kill its process tree,
#                                      and only that, if it overruns (124) or if free RAM falls under
#                                      GUARD_MIN_FREE_MB (137). Otherwise returns cmd's exit code.
#   guard_watch <pid> [min_free_mb]    background RAM watchdog for a process you started yourself
#                                      (not through guard_run): kills that PID's tree, and only it,
#                                      when free RAM falls under the floor. Prints the watchdog's PID.
# RAM watchdog: free RAM is read every GUARD_MEM_EVERY (2) s from /proc/meminfo (Git Bash maps it
# to Windows' free physical memory, ~30 ms); under GUARD_MIN_FREE_MB (1500) the run is killed
# before Windows or WSL run out of memory and take everything else down with them.
# Works in Git Bash on Windows and on Linux. Never kills by name: only the PIDs it started.

GUARD_LOCK_DIR=${GUARD_LOCK_DIR:-${TMPDIR:-${TEMP:-/tmp}}/unitsport-heavy.lock}
GUARD_POLL=${GUARD_POLL:-10}
GUARD_BEAT=${GUARD_BEAT:-10}
GUARD_STALE=${GUARD_STALE:-45}
GUARD_MIN_FREE_MB=${GUARD_MIN_FREE_MB:-1500}
GUARD_MEM_EVERY=${GUARD_MEM_EVERY:-2}
_guard_have_lock=0
_guard_beat_pid=

_guard_windows() { [ -n "${WINDIR:-}" ] || [ -n "${windir:-}" ]; }

# free RAM in whole MB
guard_free_mb() {
  # Git Bash and Linux both have /proc/meminfo (Git Bash: MemFree = Windows' free physical memory)
  if [ -r /proc/meminfo ]; then
    awk '/^MemAvailable:/ { a = $2 } /^MemFree:/ { f = $2 } END { print int((a ? a : f) / 1024) }' /proc/meminfo
  else
    local kb
    kb=$(powershell -NoProfile -c "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory" | tr -dc '0-9')
    echo $(( kb / 1024 ))
  fi
}

guard_wait_ram() {
  local need_mb=$(( $1 * 1024 )) max=${2:-1800} waited=0 free
  while :; do
    free=$(guard_free_mb)
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
_guard_mtime() { stat -c %Y "$1" 2>/dev/null || echo 0; }

_guard_drop_lock() {
  rm -f "$GUARD_LOCK_DIR/pid" "$GUARD_LOCK_DIR/info" "$GUARD_LOCK_DIR/beat"
  rmdir "$GUARD_LOCK_DIR" 2>/dev/null
}

# why the current lock is stale, or nothing if it is live
_guard_stale_reason() {
  local pid now beat started hold
  pid=$(_guard_lock_pid); now=$(date +%s)
  if [ -z "$pid" ]; then
    # the pid file is written right after mkdir: only stale once the dir has stayed empty a while
    [ $(( now - $(_guard_mtime "$GUARD_LOCK_DIR") )) -ge 10 ] && echo "no owner recorded"
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
  if [ ! -d "$GUARD_LOCK_DIR" ]; then echo "[guard] lock free"; return 0; fi
  local now; now=$(date +%s)
  echo "[guard] held by PID $(_guard_lock_pid) ($(_guard_field what)) for $(( now - $(_guard_field started 2>/dev/null || echo "$now") ))s,"     "heartbeat $(( now - $(_guard_mtime "$GUARD_LOCK_DIR/beat") ))s ago${1:+}"
  local why; why=$(_guard_stale_reason); [ -n "$why" ] && echo "[guard] STALE: $why"
  return 0
}

guard_lock() {
  local max=${1:-3600} hold=${2:-3600} waited=0 why
  while ! mkdir "$GUARD_LOCK_DIR" 2>/dev/null; do
    why=$(_guard_stale_reason)
    if [ -n "$why" ]; then
      local pid; pid=$(_guard_lock_pid)
      # ponytail: two waiters can both judge it stale; re-reading the pid first makes the race tiny, not zero
      if [ "$(_guard_lock_pid)" = "$pid" ]; then
        echo "[guard] removing stale lock: $why" >&2
        _guard_drop_lock
      fi
      continue
    fi
    if [ "$waited" -ge "$max" ]; then
      echo "[guard] heavy-run lock still held after ${waited}s: $(guard_status | head -1)" >&2
      return 1
    fi
    [ $(( waited % 120 )) -eq 0 ] && guard_status >&2
    sleep 2; waited=$(( waited + 2 ))
  done
  echo "$BASHPID" > "$GUARD_LOCK_DIR/pid"
  printf 'started=%s
max_hold=%s
what=%s
' "$(date +%s)" "$hold" "${GUARD_WHAT:-$0 $*}" > "$GUARD_LOCK_DIR/info"
  touch "$GUARD_LOCK_DIR/beat"
  # heartbeat: dies with its owner, so a dead or PID-reused owner goes stale in GUARD_STALE s
  local owner=$BASHPID
  ( while kill -0 "$owner" 2>/dev/null && [ -d "$GUARD_LOCK_DIR" ]; do touch "$GUARD_LOCK_DIR/beat" 2>/dev/null; sleep "$GUARD_BEAT"; done ) &
  _guard_beat_pid=$!
  _guard_have_lock=1
}

guard_unlock() {
  [ "$_guard_have_lock" = 1 ] || return 0
  [ -n "$_guard_beat_pid" ] && kill "$_guard_beat_pid" 2>/dev/null
  _guard_beat_pid=
  [ "$(_guard_lock_pid)" = "$BASHPID" ] && _guard_drop_lock
  _guard_have_lock=0
}

# a PID and all its descendants as Git Bash / Linux sees them (ps -ef pid/ppid), the PID first
_guard_tree() {
  ps -ef 2>/dev/null | awk -v root="$1" 'NR > 1 { kids[$3] = kids[$3] " " $2 }
    END { q = root; while (q != "") { n = split(q, a, " "); q = ""; for (i = 1; i <= n; i++) { print a[i]; q = q kids[a[i]] } } }'
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
    kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
    sleep 2
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
    for p in $(_guard_tree "$pid"); do kill -KILL "$p" 2>/dev/null; done
  fi
}

guard_run() {
  local timeout_s=$1 log=$2 pid elapsed=0 free
  shift 2
  mkdir -p "$(dirname "$log")"
  # its own process group on Linux, so the timeout can take its children down with it
  if command -v setsid > /dev/null && ! _guard_windows; then
    setsid "$@" > "$log" 2>&1 < /dev/null &
  else
    "$@" > "$log" 2>&1 < /dev/null &
  fi
  pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$elapsed" -ge "$timeout_s" ]; then
      echo "[guard] timeout after ${timeout_s}s, killing PID $pid and its children" >> "$log"
      _guard_kill_tree "$pid"
      wait "$pid" 2>/dev/null
      return 124
    fi
    if [ $(( elapsed % GUARD_MEM_EVERY )) -eq 0 ]; then
      free=$(guard_free_mb)
      if [ "$free" -lt "$GUARD_MIN_FREE_MB" ]; then
        echo "[guard] only ${free} MB free (floor ${GUARD_MIN_FREE_MB} MB): killing PID $pid and its children" | tee -a "$log" >&2
        _guard_kill_tree "$pid"
        wait "$pid" 2>/dev/null
        return 137
      fi
    fi
    sleep 1; elapsed=$(( elapsed + 1 ))
  done
  wait "$pid"
}

guard_watch() {
  local target=$1 floor=${2:-$GUARD_MIN_FREE_MB}
  (
    while kill -0 "$target" 2>/dev/null; do
      free=$(guard_free_mb)
      if [ "$free" -lt "$floor" ]; then
        echo "[guard] only ${free} MB free (floor ${floor} MB): killing PID $target and its children" >&2
        _guard_kill_tree "$target"
        exit 0
      fi
      sleep "$GUARD_MEM_EVERY"
    done
  ) &
  echo $!
}
