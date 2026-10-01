# Resource guard for heavy test runs (docs/notes/general/testing.md). Source it, then:
#   guard_wait_ram <GB> [max_wait_s]   wait until that much RAM is free (fails after max_wait_s)
#   guard_lock [max_wait_s]            take the machine-wide heavy-run lock, queueing behind others
#   guard_unlock                       release it (put it in your EXIT trap; a lock left by a
#                                      dead process is taken over as stale anyway)
#   guard_run <timeout_s> <log> cmd... run cmd with its output in log; kill its process tree,
#                                      and only that, if it overruns. Returns cmd's exit code, 124 on timeout.
# Works in Git Bash on Windows and on Linux. Never kills by name: only the PIDs it started.

GUARD_LOCK_DIR=${GUARD_LOCK_DIR:-${TMPDIR:-${TEMP:-/tmp}}/unitsport-heavy.lock}
GUARD_POLL=${GUARD_POLL:-10}
_guard_have_lock=0

_guard_windows() { [ -n "${WINDIR:-}" ] || [ -n "${windir:-}" ]; }

# free RAM in whole MB
guard_free_mb() {
  if _guard_windows; then
    local kb
    kb=$(powershell -NoProfile -c "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory" | tr -dc '0-9')
    echo $(( kb / 1024 ))
  else
    awk '/^MemAvailable:/ { print int($2 / 1024) }' /proc/meminfo
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

_guard_drop_lock() {
  rm -f "$GUARD_LOCK_DIR/pid"
  rmdir "$GUARD_LOCK_DIR" 2>/dev/null
}

guard_lock() {
  local max=${1:-3600} waited=0 pid
  while ! mkdir "$GUARD_LOCK_DIR" 2>/dev/null; do
    pid=$(_guard_lock_pid)
    # stale: its owner died without releasing it. The pid file is written right after mkdir, so
    # a lock without one is only stale once it has stayed that way for a while.
    if { [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; } || { [ -z "$pid" ] && [ "$waited" -ge 10 ]; }; then
      # ponytail: two waiters can both judge it stale; re-reading the pid first makes the race tiny, not zero
      if [ "$(_guard_lock_pid)" = "$pid" ]; then
        echo "[guard] removing stale lock of PID ${pid:-?}" >&2
        _guard_drop_lock
      fi
      continue
    fi
    if [ "$waited" -ge "$max" ]; then
      echo "[guard] heavy-run lock still held by PID $pid after ${waited}s" >&2
      return 1
    fi
    [ "$waited" -eq 0 ] && echo "[guard] another heavy run holds the lock (PID $pid), queueing" >&2
    sleep 2; waited=$(( waited + 2 ))
  done
  echo "$BASHPID" > "$GUARD_LOCK_DIR/pid"
  _guard_have_lock=1
}

guard_unlock() {
  [ "$_guard_have_lock" = 1 ] || return 0
  [ "$(_guard_lock_pid)" = "$BASHPID" ] && _guard_drop_lock
  _guard_have_lock=0
}

# kill one PID we started, with everything it started
_guard_kill_tree() {
  local pid=$1
  if _guard_windows && [ -r "/proc/$pid/winpid" ]; then
    taskkill //F //T //PID "$(cat "/proc/$pid/winpid")" > /dev/null 2>&1
  else
    kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
    sleep 2
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
  fi
}

guard_run() {
  local timeout_s=$1 log=$2 pid elapsed=0
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
    sleep 1; elapsed=$(( elapsed + 1 ))
  done
  wait "$pid"
}
