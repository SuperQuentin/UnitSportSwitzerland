#!/usr/bin/env bash
# The playtest suite (#751, #844; docs/notes/general/playtest.md).
#   tools/playtest.sh [batch] [course] [-- game args]
#                                               build, then play the scenarios on a fixture course (flat by default),
#                                               recorded (--playtest-record): F12 notes what is wrong, End session
#                                               writes test_output/playtest/sessions/<time>/report.md for Claude
#   tools/playtest.sh retest [course]           the same, with only the scenarios due: failed, stale, never played
#   tools/playtest.sh live [course]             the live loop: Claude listens through the MCP server (.mcp.json), /playtest
#   tools/playtest.sh pending                   the scenarios due: never played, or their code changed since
#   tools/playtest.sh list                      every scenario with its standing
#   tools/playtest.sh last                      the newest session's folder
#   tools/playtest.sh report [dir]              write a session's report.md again (after a crash; newest session by default)
#   tools/playtest.sh topview [dir] [args]      a session's top view (--from --to --at --around E,N --radius --out --html,
#                                               docs/notes/general/topview.md), newest session by default
#   tools/playtest.sh relaunch <pid> build|nobuild <godot> <args...>
#                                               used by the game itself (Claude's `restart` tool): waits for the game
#                                               to quit, rebuilds, starts it again; not meant to be typed
# Env: GODOT (the Godot executable, see docs/notes/general/godot-exe.md).
set -u
cd "$(dirname "$0")/.."

godot_exe() {
  if [ -n "${GODOT:-}" ]; then echo "$GODOT"; return; fi
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
      for g in "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe \
               /c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe; do
        [ -f "$g" ] && { echo "$g"; return; }
      done ;;
  esac
  echo godot
}

alive() {
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) tasklist //FI "PID eq $1" 2>/dev/null | grep -q " $1 " ;;
    *) kill -0 "$1" 2>/dev/null ;;
  esac
}

# the newest recorded session, or the folder given
session() {
  if [ -n "${1:-}" ] && [ "${1#--}" = "$1" ]; then echo "$1"; return; fi
  ls -1d test_output/playtest/sessions/*/ 2>/dev/null | sort | tail -1 | sed 's#/$##'
}

# play: build, then the game on a fixture course; $1 the course (or --), then game args
play() {
  local mode=$1 course=flat
  shift
  if [ $# -gt 0 ] && [ "$1" != -- ]; then course=$1; shift; fi
  [ "${1:-}" = -- ] && shift
  dotnet build UnitSportSwitzerland.csproj -v q -nologo || exit 1
  local flags=(--playtest)
  case $mode in
    batch) flags+=(--playtest-batch --playtest-record)
           echo "Batch playtest on the '$course' course: F12 notes what is wrong, End session in the panel (F10) when done." ;;
    retest) flags+=(--playtest-batch --playtest-record --playtest-retest)
            echo "Retest on the '$course' course: only the scenarios due." ;;
    live) echo "Live playtest on the '$course' course. In Claude Code (in this repo): /playtest" ;;
  esac
  exec "$(godot_exe)" --path . -- --world fixture --chunks "fixture:$course" "${flags[@]}" "$@"
}

mkdir -p test_output/playtest
case "${1:-}" in
  pending | list)
    which=$([ "$1" = list ] && echo all || echo due)
    "$(godot_exe)" --headless --path . -- --playtest-list "$which" 2>&1 | grep -a '^\[playtest-list\]' | sed 's/^\[playtest-list\] //'
    ;;
  last)
    session
    ;;
  report)
    dir=$(session "${2:-}")
    [ -n "$dir" ] || { echo "no recorded session in test_output/playtest/sessions" >&2; exit 1; }
    "$(godot_exe)" --headless --path . -- --playtest-report "$dir" 2>&1 | grep -a '^\[playtest\]'
    ;;
  topview)
    shift
    dir=$(session "${1:-}")
    [ -n "${1:-}" ] && [ "${1#--}" = "$1" ] && shift
    [ -n "$dir" ] || { echo "no recorded session in test_output/playtest/sessions" >&2; exit 1; }
    "$(godot_exe)" --headless --path . -- --topview "$dir" "$@" 2>&1 | grep -a '^\[topview\]'
    ;;
  relaunch)
    pid=$2 build=$3 exe=$4
    shift 4
    # the game quits a moment after asking; give it 30 s
    for _ in $(seq 1 60); do alive "$pid" || break; sleep 0.5; done
    if [ "$build" = build ]; then
      dotnet build UnitSportSwitzerland.csproj > test_output/playtest/build.log 2>&1 || echo "Build FAILED" >> test_output/playtest/build.log
    fi
    # detached, so this helper can end
    nohup "$exe" "$@" > test_output/playtest/game.log 2>&1 &
    ;;
  batch | retest | live)
    mode=$1
    shift
    play "$mode" "$@"
    ;;
  *)
    play batch "$@"
    ;;
esac
