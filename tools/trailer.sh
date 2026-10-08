#!/usr/bin/env bash
# The trailer (#706): preview its shots, frame them, or film the whole thing and cut it on its
# song. One game launch per run; the director stages each shot. The films: FILM=story (default,
# docs/trailer/script.md, StoryScript.cs), FILM=showcase (docs/trailer/storyboard.md,
# TrailerScript.cs) and FILM=drognens (the music clip, docs/trailer/drognens.md, DrognensScript.cs).
#
#   tools/trailer.sh preview [shots]   play in a window, each shot with its part of the song
#   tools/trailer.sh stills  [shots]   first, middle and last frame of each shot as PNGs
#   tools/trailer.sh render  [shots]   film the shots (1920x1080, 30 fps, frame-exact) and cut them
#                                      on the song, as the film's next version; a few shots are
#                                      filmed again and cut with the last version's others
#   tools/trailer.sh cut               cut the newest version again (after replacing a shot by hand)
#
# Every render is a version of its own, never written over: <trailers>/<film>/v1, v2, ... each with
# trailer.mp4, trailer_share.mp4 (lighter, to send), its shots/, render.log and version.txt (when,
# which commit, which shots were filmed). <trailers> is the main checkout's test_output/trailer, so
# the films outlive the worktree they were made in. Stills are scratch, in this checkout's
# test_output/trailer/<film>/stills.
#
# shots: all (default), 5, 5-9 or 5,7,12 (shot numbers of the film).
# Env: GODOT (default godot), CHUNKS (terrain; default ./terrain_chunks, or the main checkout's
# from a worktree), SIZE (default 1920x1080), LOG=1 prints every actor twice a second,
# TRAILERS (where the versions go), VERSION (which one `cut` cuts; default the newest).
# The film's song (incompetech.com, CC BY 4.0: "Voxel Revolution" for the trailers, "I Got a Stick
# Feat James Gavins" for the clip) is downloaded once into <trailers>/ (not committed). Needs ffmpeg
# on PATH (or bin/).
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:-preview}
shots=${2:-all}
GODOT=${GODOT:-godot}
SIZE=${SIZE:-1920x1080}
FILM=${FILM:-story}
main="$(git worktree list --porcelain | sed -n '1s/^worktree //p')"
TRAILERS=${TRAILERS:-$main/test_output/trailer}
films=$TRAILERS/$FILM
scratch=test_output/trailer/$FILM
mkdir -p "$films" "$scratch"
case $FILM in
  drognens) title="I Got a Stick Feat James Gavins" ;;
  *) title="Voxel Revolution" ;;
esac
song=$TRAILERS/$(echo "$title" | tr 'A-Z ' 'a-z_').mp3

if [ -z "${CHUNKS:-}" ]; then
  if [ -f terrain_chunks/manifest.json ]; then CHUNKS=terrain_chunks
  else CHUNKS="$main/terrain_chunks"
  fi
fi

# the song only for what plays or cuts it
if [ "$mode" != stills ] && [ ! -f "$song" ]; then
  curl -fsSL -o "$song" "https://incompetech.com/music/royalty-free/mp3-royaltyfree/${title// /%20}.mp3"
fi

log=()
[ "${LOG:-}" = 1 ] && log=(--trailer-log)
game=(--chunks "$CHUNKS" --title "trailer $mode $shots" --trailer "$shots" --trailer-film "$FILM" --trailer-size "$SIZE" "${log[@]}")

# the newest version's number, 0 when there is none yet
newest() {
  local n
  n=$(ls -d "$films"/v[0-9]* 2>/dev/null | sed 's#.*/v##' | sort -n | tail -1 || true)
  echo "${n:-0}"
}

# cuts version folder $1's shots on the song: trailer.mp4, and trailer_share.mp4 to send
cut_film() {
  local out=$1 first start
  ls "$out"/shots/shot*.mp4 > /dev/null
  # the song from where the first shot of the cut starts (the director writes shotNN.start)
  first=$(ls "$out"/shots/shot*.mp4 | head -1)
  start=$(cat "${first%.mp4}.start" 2>/dev/null || echo 0)
  : > "$out/shots/list.txt"
  for f in "$out"/shots/shot*.mp4; do echo "file '$(basename "$f")'" >> "$out/shots/list.txt"; done
  ffmpeg -y -loglevel error -f concat -safe 0 -i "$out/shots/list.txt" -ss "$start" -i "$song" \
    -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart "$out/trailer.mp4"
  ffmpeg -y -loglevel error -i "$out/trailer.mp4" -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 160k \
    -movflags +faststart "$out/trailer_share.mp4"
  echo "wrote $out/trailer.mp4 and trailer_share.mp4 ($(ls "$out"/shots/shot*.mp4 | wc -l) shots, the song from ${start} s)"
}

case $mode in
  preview)
    "$GODOT" --path . --resolution 1280x720 -- "${game[@]}" --trailer-song "$song"
    ;;
  stills)
    rm -rf "$scratch/stills"
    "$GODOT" --path . --resolution 1280x720 -- "${game[@]}" --trailer-stills "$scratch/stills" | tee "$scratch/stills.log" | grep '\[trailer\]'
    echo "stills in $scratch/stills"
    ;;
  render)
    last=$(newest)
    out=$films/v$((last + 1))
    mkdir -p "$out/shots"
    filmed=all
    if [ "$shots" != all ]; then
      # a few shots filmed again: the others are the last version's
      if [ "$last" -eq 0 ]; then
        echo "no earlier $FILM version to take the other shots from: render all first" >&2
        rmdir "$out/shots" "$out"
        exit 1
      fi
      cp "$films/v$last"/shots/shot* "$out/shots/"
      filmed="$shots (the others from v$last)"
    fi
    # --fixed-fps: one frame is one 30th of game time, however slowly a frame renders
    "$GODOT" --path . --resolution 1280x720 --fixed-fps 30 -- "${game[@]}" --trailer-record "$out/shots" \
      | tee "$out/render.log" | grep '\[trailer\]' || true
    changed=$(git status --porcelain -- src tools | grep -q . && echo ", with local changes" || true)
    {
      echo "film: $FILM"
      echo "version: v$((last + 1))"
      echo "made: $(date '+%Y-%m-%d %H:%M')"
      echo "commit: $(git rev-parse --short HEAD) on $(git branch --show-current)$changed"
      echo "filmed: $filmed"
    } > "$out/version.txt"
    cut_film "$out"
    ;;
  cut)
    version=${VERSION:-$(newest)}
    [ -d "$films/v$version" ] || { echo "no $FILM version v$version in $films" >&2; exit 1; }
    cut_film "$films/v$version"
    ;;
  *)
    echo "usage: [FILM=story|showcase] tools/trailer.sh preview|stills|render [shots] | cut" >&2
    exit 2
    ;;
esac
