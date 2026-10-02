# Two-client loopback checks (tools/*check.sh, docs/notes/general/twoclient-checks.md): a dedicated
# server, clients A and B, then the RESULT lines. Source it first thing:
#   . "$(dirname "$0")/lib/twoclient.sh" <name>
# It sources guard.sh, takes the heavy-run lock (unless GUARD_LOCK_HELD, as under tools/test.sh) and
# waits for 4 GB free, cds to the repo root and sets:
#   OUT     test_output
#   GODOT   the env value, else the Godot console exe on Windows (docs/notes/general/godot-exe.md), else godot
#   CH      (--chunks "$CHUNKS") when CHUNKS is set, else empty: pass it as ${CH[@]+"${CH[@]}"}
#   APPDATA / XDG_DATA_HOME  a fresh $OUT/userdata_<name>, so user:// (loot, bank, placed, birds...) is
#           never the real one; USERDATA=<dir> picks another, USERDATA=real keeps the real one
# Then:
#   tc_server <timeout_s> <wait_s> <log> args...   a headless server ($GODOT --headless --path . -- args)
#                                    in the background, its PID in SERVER; returns once its log says
#                                    "server listening" (or after wait_s, or when it died)
#   tc_client <timeout_s> <log> [--windowed] args...  a client, headless unless --windowed or WINDOWED=1;
#                                    runs in the foreground: add & (and $!) for a background one
#   tc_godot <timeout_s> <log> godot-args...  any other Godot run (a self-test), under guard_run
#   tc_stop [pid]                    kill a server (default $SERVER) and every process under it, by PID
#   tc_ok <n> <logs...>              true when the logs hold exactly n "RESULT: ok" lines
# Every run goes through guard_run (timeout and RAM floor, tree kill by PID); the servers left at
# exit are killed by the EXIT trap. A headless Godot may exit 139 after its result: read RESULT lines.
_tc_name=${1:?usage: . lib/twoclient.sh <name>}
. "$(dirname "${BASH_SOURCE[0]}")/guard.sh"
guard_watch $$ > /dev/null   # RAM watchdog for this script's own processes
set -u
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
OUT=test_output
mkdir -p "$OUT"

# the winget `godot` link hangs: on Windows default to the real console exe, as tools/test.sh does
if [ -z "${GODOT:-}" ] && [ -n "${LOCALAPPDATA:-}" ]; then
  for g in "$(cygpath -u "$LOCALAPPDATA" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe \
           /c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.1-stable_mono_win64/godot_v4.7.1-stable_mono_win64_console.exe; do
    [ -f "$g" ] && { GODOT=$g; break; }
  done
fi
GODOT=${GODOT:-godot}
CH=(); [ -n "${CHUNKS:-}" ] && CH=(--chunks "$CHUNKS")

# user:// of every Godot started here: Windows reads APPDATA, Linux XDG_DATA_HOME
if [ "${USERDATA:-}" != real ]; then
  _tc_ud=${USERDATA:-$OUT/userdata_$_tc_name}
  [ -z "${USERDATA:-}" ] && [ -d "$_tc_ud" ] && find "$_tc_ud" -mindepth 1 -delete
  mkdir -p "$_tc_ud"
  _tc_ud=$(cd "$_tc_ud" && pwd)
  if _guard_windows; then export APPDATA="$(cygpath -w "$_tc_ud")"; else export XDG_DATA_HOME="$_tc_ud"; fi
  echo "[$_tc_name] user:// under $_tc_ud"
fi

_tc_servers=()
_tc_cleanup() {
  local p
  for p in "${_tc_servers[@]}"; do _guard_kill_tree "$p"; done
  [ -n "${GUARD_LOCK_HELD:-}" ] || guard_unlock
}
trap _tc_cleanup EXIT
# under tools/test.sh the runner already holds the lock: taking it again would wait forever
if [ -z "${GUARD_LOCK_HELD:-}" ]; then guard_lock 1800 "${TC_HOLD:-1500}" || exit 1; fi
guard_wait_ram 4 600 || exit 1

tc_server() {
  local timeout_s=$1 wait_s=$2 log=$3 i
  shift 3
  : > "$log"
  guard_run "$timeout_s" "$log" "$GODOT" --headless --path . -- "$@" &
  SERVER=$!
  _tc_servers+=("$SERVER")
  for i in $(seq 1 "$wait_s"); do
    grep -q "server listening" "$log" 2>/dev/null && return 0
    kill -0 "$SERVER" 2>/dev/null || return 1
    sleep 1
  done
}

tc_client() {
  local timeout_s=$1 log=$2 headless=--headless
  shift 2
  [ "${1:-}" = --windowed ] && { headless=; shift; }
  [ -n "${WINDOWED:-}" ] && headless=
  guard_run "$timeout_s" "$log" "$GODOT" $headless --path . -- "$@"
}

tc_godot() { local timeout_s=$1 log=$2; shift 2; guard_run "$timeout_s" "$log" "$GODOT" "$@"; }

tc_stop() { local p=${1:-$SERVER}; _guard_kill_tree "$p"; wait "$p" 2>/dev/null; return 0; }

tc_ok() { local n=$1; shift; [ "$(grep -h "RESULT: ok" "$@" 2>/dev/null | wc -l)" -eq "$n" ]; }
