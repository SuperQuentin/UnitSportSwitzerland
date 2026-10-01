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
#                                      and only that, if it overruns. Returns cmd's exit code, 124 on timeout.
# Works in Git Bash on Windows and on Linux. Never kills by name: only the PIDs it started.

GUARD_LOCK_DIR=${GUARD_LOCK_DIR:-${TMPDIR:-${TEMP:-/tmp}}/unitsport-heavy.lock}
GUARD_POLL=${GUARD_POLL:-10}
GUARD_BEAT=${GUARD_BEAT:-10}
GUARD_STALE=${GUARD_STALE:-45}
_guard_have_lock=0
_guard_beat_pid=

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
