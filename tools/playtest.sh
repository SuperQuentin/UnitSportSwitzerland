#!/usr/bin/env bash
# The playtest suite (#751, docs/notes/general/playtest.md).
#   tools/playtest.sh [course] [-- game args]  build, then play the scenarios on a fixture course (flat by default),
#                                               with the MCP server Claude Code connects to (.mcp.json)
#   tools/playtest.sh pending                  the scenarios due: never played, or their code changed since
#   tools/playtest.sh list                     every scenario with its standing
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
      for g in "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe \
               /c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.1-stable_mono_win64/godot_v4.7.1-stable_mono_win64_console.exe; do
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

mkdir -p test_output/playtest
case "${1:-}" in
  pending | list)
    which=$([ "$1" = list ] && echo all || echo due)
    "$(godot_exe)" --headless --path . -- --playtest-list "$which" 2>&1 | grep -a '^\[playtest-list\]' | sed 's/^\[playtest-list\] //'
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
  *)
    course=${1:-flat}
    [ "${1:-}" = -- ] && course=flat
    shift $(( $# > 0 ? 1 : 0 ))
    [ "${1:-}" = -- ] && shift
    dotnet build UnitSportSwitzerland.csproj -v q -nologo || exit 1
    echo "Playtest on the '$course' course. In Claude Code (in this repo): /playtest"
    exec "$(godot_exe)" --path . -- --world fixture --chunks "fixture:$course" --playtest "$@"
    ;;
esac
