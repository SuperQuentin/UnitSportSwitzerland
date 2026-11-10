#!/usr/bin/env bash
# Reference screenshots (#1429): every view of tests/refshots/views.txt in every style, with a pinned
# render profile, for comparing a change with main (tools/refshot-diff.py). docs/notes/general/regression-net.md
#
#   tools/refshots.sh <tag>                     e.g. tools/refshots.sh main-51811701
#   tools/refshots.sh --frame earth <tag>       the earth world (G2 C6, #1500): the same views through Earth positions
#   tools/refshot-parity.sh <tag>               flat and earth one after the other, then the earth-vs-flat diff
#
# Writes $REFSHOTS/<tag>/<style>/<view>.png, <style>.log and meta.json. Env:
#   REFSHOTS  where the libraries live (D:/UnitSportSwitzerland/refshots)
#   CHUNKS    the real terrain (D:/UnitSportSwitzerland/terrain_chunks)
#   STYLES    "cartoon ps1 real-" (any of cartoon ps1 real- real+)
#   VIEWS     names to take only those (default every view)
#   MODELS    1: also the model viewer's PNG run per style ($tag/<style>/models/), ~1000 models, a few minutes
#   SETTLE    least settle per shot, s (8; each shot also waits for its tiles, src/Core/ShotRunner.cs)
#   SIZE      the window (1600x900)
#   RENDER    the settings on top of the defaults (as tools/trailer.sh: rings, detail, horizon, fog, AA)
#   SYSTEMS   the client's systems (Core/Systems): everything but what moves by itself
#   ARGS      more game arguments, e.g. ARGS="--curvature 0" for a branch's flat picture
#   GODOT     the Godot executable (as tools/test.sh)
#   ANCHORS   1: one launch per date and anchor (views within 6 km of a whole km, tools/refshot-poses.py), the game
#             started there (--at, --origin): the flat side of an earth comparison. Always so under --frame earth
#   LEAVES    --frame earth: the cube leaves (D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube)
#   GLOBE     the cube_global pyramid (--globe); unset, both frames take the game's (Globe.DefaultDir,
#             D:/UnitSportSwitzerland/cube_global: built from B4's leaves since #1654, test_b3/b4/cube_global is older)
#   RESUME    1: only the views with no picture yet in <tag> (a launch the RAM guard killed); a kept log stays
#   HEIGHTS   --frame earth: a flat library (<root>/<tag>) whose logs give each "g" view's altitude, so both worlds
#             stand at the same point (without it a "g" view stands above the earth ground)
# Every run plays in a fresh user:// of its own (no player's settings), muted (Dummy audio), at low
# priority, never on top; only the compiled shader caches carry over between runs. The waves stand
# still (--watertime): a lake differed by up to 65 % of its pixels between two runs of one commit.
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
FRAME=${FRAME:-flat}
while [ $# -gt 0 ]; do
  case $1 in
    --frame) FRAME=$2; shift 2 ;;
    --frame=*) FRAME=${1#--frame=}; shift ;;
    *) break ;;
  esac
done
tag=${1:?usage: tools/refshots.sh [--frame flat|earth] <tag>}
case $FRAME in flat|earth) ;; *) echo "--frame flat or earth, not $FRAME" >&2; exit 2 ;; esac
[ "$FRAME" = earth ] && ANCHORS=1
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
ROOT=${REFSHOTS:-D:/UnitSportSwitzerland/refshots}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}
STYLES=${STYLES:-cartoon ps1 real-}
SETTLE=${SETTLE:-8}
SIZE=${SIZE:-1600x900}
RENDER=${RENDER:---rings 6 --detail high --horizon 150 --fog off --vsync off --render-scale 1 --aa fxaa}
# what moves on its own (traffic, trains, people, birds, loot) is left out: it would differ between two runs
SYSTEMS=${SYSTEMS:-terrain,generated,physics,sky,interiors,occasions,ui}
DATE0=2026-07-15
HOUR0=14
if [ -z "${GODOT:-}" ] && _guard_windows; then
  for g in "$(cygpath -u "${LOCALAPPDATA:-}" 2>/dev/null)"/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_*/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe \
           /c/ProgramData/chocolatey/lib/godot-mono/tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe; do
    [ -f "$g" ] && { GODOT=$g; break; }
  done
fi
GODOT=${GODOT:-godot}
[ -d "$CHUNKS" ] || { echo "no terrain at $CHUNKS (CHUNKS=)" >&2; exit 2; }
if [ "$FRAME" = earth ]; then [ -d "$LEAVES" ] || { echo "no leaves at $LEAVES (LEAVES=)" >&2; exit 2; }; fi

out=$ROOT/$tag
mkdir -p "$out"
scratch=test_output/refshots/$tag
mkdir -p "$scratch"
caches=test_output/refshots/profile_caches
mkdir -p "$caches"

# the queues, one per launch (tools/refshot-poses.py): "<queue> <date> <anchorE> <anchorN> <shots>" a line. Without
# ANCHORS every queue is relative to the catalogue's first view's whole km, also for a VIEWS subset (from another
# origin the floats round otherwise: 8 % of a shot's pixels differed)
poses() { # style dir
  local o=(tests/refshots/views.txt "$scratch/q_$1" "$2" --frame "$FRAME" --settle "$SETTLE" --hour "$HOUR0" --date "$DATE0")
  [ "${ANCHORS:-0}" = 1 ] && o+=(--anchors)
  [ -n "${VIEWS:-}" ] && o+=(--views "$VIEWS")
  [ -n "${HEIGHTS:-}" ] && o+=(--heights "$HEIGHTS/$1")
  [ "${RESUME:-0}" = 1 ] && o+=(--resume)
  rm -rf "$scratch/q_$1"
  python tools/refshot-poses.py "${o[@]}"
}

profile_env() { # dir: a fresh user:// there, the shader caches copied in; prints the env assignment
  local d=$1 userdir
  rm -rf "$d"; mkdir -p "$d"; d=$(cd "$d" && pwd)
  case "$(uname -s)" in
    Darwin) userdir="$d/Library/Application Support/Godot/app_userdata/UnitSportSwitzerland"; echo "HOME=$d" ;;
    MINGW*|MSYS*|CYGWIN*) userdir="$d/Godot/app_userdata/UnitSportSwitzerland"; echo "APPDATA=$(cygpath -w "$d")" ;;
    *) userdir="$d/godot/app_userdata/UnitSportSwitzerland"; echo "XDG_DATA_HOME=$d" ;;
  esac
  mkdir -p "$userdir"
  for c in shader_cache vulkan; do [ -d "$caches/$c" ] && cp -R "$caches/$c" "$userdir/"; done
  echo "$userdir" > "$d/.userdir"
}
keep_caches() { # profile dir
  local userdir; userdir=$(cat "$1/.userdir")
  for c in shader_cache vulkan; do [ -d "$userdir/$c" ] && { rm -rf "${caches:?}/$c"; cp -R "$userdir/$c" "$caches/"; }; done
  return 0
}

read -r -a render <<< "$RENDER"
extra=(); [ -n "${ARGS:-}" ] && read -r -a extra <<< "$ARGS"
[ "$FRAME" = earth ] && extra+=(--frame earth --leaves "$LEAVES")
[ -n "${GLOBE:-}" ] && extra+=(--globe "$GLOBE")
style_flag() { case $1 in real-) echo real- ;; real+) echo real+ ;; *) echo "$1" ;; esac; }
t_all=$SECONDS
for style in $STYLES; do
  mkdir -p "$out/$style"
  dirw=$(cd "$out/$style" && { pwd -W 2>/dev/null || pwd; })
  # Python prints CRLF on Windows: a "1\r" is no number to [ ]
  launches=$(poses "$style" "$dirw" | tr -d '\r') || { echo "[refshots] no queues" >&2; exit 2; }
  while read -r q date ae an shots; do
    [ "$shots" -gt 0 ] || continue
    prof=$scratch/profile_$style
    envset=$(profile_env "$prof" | head -1)
    at=(--origin "$ae,$an"); log=$out/$style/$style.$date.log
    if [ "${ANCHORS:-0}" = 1 ]; then at=(--at "$ae,$an" --origin "$ae,$an"); log=$out/$style/$style.$date.${ae}_$an.log; fi
    # a retake keeps the earlier log (its "wrote ... at" lines are HEIGHTS' altitudes)
    n=1; while [ -f "$log" ]; do log=${log%.log}; log=${log%.r[0-9]*}.r$n.log; n=$((n + 1)); done
    echo "[refshots] $style $date ${at[*]}: $shots views"
    guard_wait_ram 6 7200 || { echo "[refshots] skipped: no 6 GB free (RESUME=1 retakes it)"; continue; }
    # shellcheck disable=SC2086
    guard_run 3600 "$log" env "$envset" "$GODOT" --resolution "$SIZE" --position 24,48 --audio-driver Dummy --path . -- \
      --chunks "$CHUNKS" --shot-queue "$q" "${at[@]}" --nohud --nocapture --date "$date" --time "$HOUR0" \
      --style "$(style_flag "$style")" --systems "$SYSTEMS" --watertime 100 --title "refshots $tag $style" "${render[@]}" ${extra[@]+"${extra[@]}"} < /dev/null || echo "[refshots] exit $?"
    keep_caches "$prof"
    # || true: a launch killed before its first shot has none, and pipefail would end the run
    grep -aE '\[shot\] (wrote|FAILED)|WARNING' "$log" | sed 's/^/  /' | tail -40 || true
    grep -aE 'SHADER ERROR|ERROR:' "$log" | head -5 | sed 's/^/  /' || true
  done <<< "$launches"
  if [ "${MODELS:-0}" = 1 ]; then
    prof=$scratch/profile_models_$style
    envset=$(profile_env "$prof" | head -1)
    mkdir -p "$out/$style/models"
    echo "[refshots] $style: the model viewer"
    guard_run 3600 "$out/$style/models.log" env "$envset" "$GODOT" --resolution 960x720 --audio-driver Dummy --path . -- \
      --models,"$out/$style/models" --style "$(style_flag "$style")" ${extra[@]+"${extra[@]}"} || echo "[refshots] models exit $?"
    keep_caches "$prof"
    grep -a "RESULT" "$out/$style/models.log" | tail -1 | sed 's/^/  /' || true
  fi
done

# what the pictures were taken with
gpu=$(grep -ahoE 'Vulkan [0-9.]+ - [^-]+ - Using Device #[0-9]+: [^\r]+' "$out"/*/*.log 2>/dev/null | head -1 || true)
cat > "$out/meta.json" <<J
{
  "tag": "$tag",
  "commit": "$(git rev-parse --short HEAD)$(git status --porcelain -- src shaders | grep -q . && echo +dirty || true)",
  "date": "$(date -u +%Y-%m-%dT%H:%MZ)",
  "styles": "$STYLES",
  "size": "$SIZE",
  "render": "$RENDER",
  "args": "${ARGS:-}",
  "frame": "$FRAME",
  "anchors": "${ANCHORS:-0}",
  "leaves": "$([ "$FRAME" = earth ] && echo "$LEAVES")",
  "globe": "${GLOBE:-}",
  "heights": "${HEIGHTS:-}",
  "settle": $SETTLE,
  "models": "${MODELS:-0}",
  "gpu": "$gpu",
  "seconds": $((SECONDS - t_all))
}
J
echo "[refshots] $(find "$out" -name '*.png' -not -path '*/diff-*' | wc -l) pictures in $out ($((SECONDS - t_all)) s)"
