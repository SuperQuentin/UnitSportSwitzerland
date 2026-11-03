#!/usr/bin/env bash
# Reference screenshots (#1429): every view of tests/refshots/views.txt in every style, with a pinned
# render profile, for comparing a change with main (tools/refshot-diff.py). docs/notes/general/regression-net.md
#
#   tools/refshots.sh <tag>                     e.g. tools/refshots.sh main-51811701
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
# Every run plays in a fresh user:// of its own (no player's settings), muted (Dummy audio), at low
# priority, never on top; only the compiled shader caches carry over between runs. The waves stand
# still (--watertime): a lake differed by up to 65 % of its pixels between two runs of one commit.
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/lib/guard.sh
tag=${1:?usage: tools/refshots.sh <tag>}
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

out=$ROOT/$tag
mkdir -p "$out"
scratch=test_output/refshots/$tag
mkdir -p "$scratch"
caches=test_output/refshots/profile_caches
mkdir -p "$caches"

# the views, as "name class E N height pitch bearing hour date"
views=$(grep -vE '^\s*(#|$)' tests/refshots/views.txt | awk -v h="$HOUR0" -v d="$DATE0" \
  '{ printf "%s %s %s %s %s %s %s %s %s\n", $1, $2, $3, $4, $5, $6, $7, ($8 == "" ? h : $8), ($9 == "" ? d : $9) }')
# the origin every queue's coordinates are relative to: the catalogue's first view's whole km, also
# for a VIEWS subset (from another origin the floats round otherwise: 8 % of a shot's pixels differed)
OE=$(awk 'NR == 1 { printf "%d", int($3 / 1000) * 1000 }' <<< "$views")
ON=$(awk 'NR == 1 { printf "%d", int($4 / 1000) * 1000 }' <<< "$views")
if [ -n "${VIEWS:-}" ]; then
  views=$(awk -v want=" $VIEWS " 'index(want, " " $1 " ")' <<< "$views")
fi
[ -n "$views" ] || { echo "no view matches VIEWS=$VIEWS" >&2; exit 2; }
dates=$(awk '{ print $9 }' <<< "$views" | sort -u)

# one queue per date: x,y,z,pitch,yaw,settle,path per view (yaw = -bearing), a /time line where the hour changes
queue() { # date style dir
  awk -v date="$1" -v dir="$3" -v oe="$OE" -v on="$ON" -v s="$SETTLE" '
    $9 != date { next }
    {
      if ($8 != hour) { hour = $8; h = int(hour); m = int((hour - h) * 60 + 0.5); printf "/time set %d:%02d\n", h, m }
      h3 = $5; y = (h3 ~ /^[gi]/) ? h3 : h3 + 0
      printf "%d,%s,%d,%s,%s,%s,%s/%s.png\n", $3 - oe, y, on - $4, $6, -$7, s, dir, $1
    }
    END { print "quit" }' <<< "$views"
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
style_flag() { case $1 in real-) echo real- ;; real+) echo real+ ;; *) echo "$1" ;; esac; }
t_all=$SECONDS
for style in $STYLES; do
  mkdir -p "$out/$style"
  for date in $dates; do
    q=$scratch/q_${style}_$date.txt
    dirw=$(cd "$out/$style" && { pwd -W 2>/dev/null || pwd; })
    queue "$date" "$style" "$dirw" > "$q"
    [ "$(grep -c png "$q")" -gt 0 ] || continue
    prof=$scratch/profile_$style
    envset=$(profile_env "$prof" | head -1)
    log=$out/$style/$style.$date.log
    echo "[refshots] $style $date: $(grep -c png "$q") views"
    guard_wait_ram 6
    # shellcheck disable=SC2086
    guard_run 3600 "$log" env "$envset" "$GODOT" --resolution "$SIZE" --position 24,48 --audio-driver Dummy --path . -- \
      --chunks "$CHUNKS" --shot-queue "$q" --origin "$OE,$ON" --nohud --nocapture --date "$date" --time "$HOUR0" \
      --style "$(style_flag "$style")" --systems "$SYSTEMS" --watertime 100 --title "refshots $tag $style" "${render[@]}" ${extra[@]+"${extra[@]}"} || echo "[refshots] exit $?"
    keep_caches "$prof"
    grep -aE '\[shot\] (wrote|FAILED)|WARNING' "$log" | sed 's/^/  /' | tail -40
    grep -aE 'SHADER ERROR|ERROR:' "$log" | head -5 | sed 's/^/  /' || true
  done
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
  "settle": $SETTLE,
  "models": "${MODELS:-0}",
  "gpu": "$gpu",
  "seconds": $((SECONDS - t_all))
}
J
echo "[refshots] $(find "$out" -name '*.png' | wc -l) pictures in $out ($((SECONDS - t_all)) s)"
