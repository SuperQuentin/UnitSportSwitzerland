#!/usr/bin/env bash
# The trailer (#706, docs/trailer/storyboard.md): preview its shots, frame them, or film the
# whole thing and cut it on its song. One game launch per run; the director stages each shot.
#
#   tools/trailer.sh preview [shots]   play in a window, each shot with its part of the song
#   tools/trailer.sh stills  [shots]   first, middle and last frame of each shot as PNGs
#   tools/trailer.sh render  [shots]   film the shots (1920x1080, 30 fps, frame-exact) and cut
#                                      test_output/trailer/trailer.mp4 with the song under it; a few
#                                      shots are filmed again and cut with the ones already there
#   tools/trailer.sh cut               cut the filmed shots again (after replacing one by hand)
#
# shots: all (default), 5, 5-9 or 5,7,12 (shot numbers, TrailerScript.cs).
# Env: GODOT (default godot), CHUNKS (terrain; default ./terrain_chunks, or the main checkout's
# from a worktree), SIZE (default 1920x1080), LOG=1 prints every actor twice a second.
# The song, "Voxel Revolution" by Kevin MacLeod (incompetech.com, CC BY 4.0), is downloaded once
# into test_output/trailer/ (not committed). Needs ffmpeg on PATH (or bin/).
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:-preview}
shots=${2:-all}
GODOT=${GODOT:-godot}
SIZE=${SIZE:-1920x1080}
out=test_output/trailer
mkdir -p "$out"
song=$out/voxel_revolution.mp3

if [ -z "${CHUNKS:-}" ]; then
  if [ -f terrain_chunks/manifest.json ]; then CHUNKS=terrain_chunks
  else CHUNKS="$(git worktree list --porcelain | sed -n '1s/^worktree //p')/terrain_chunks"
  fi
fi

# the song only for what plays or cuts it
if [ "$mode" != stills ] && [ ! -f "$song" ]; then
  curl -fsSL -o "$song" "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Voxel%20Revolution.mp3"
fi

log=()
[ "${LOG:-}" = 1 ] && log=(--trailer-log)
game=(--chunks "$CHUNKS" --title "trailer $mode $shots" --trailer "$shots" --trailer-size "$SIZE" "${log[@]}")

case $mode in
  preview)
    "$GODOT" --path . --resolution 1280x720 -- "${game[@]}" --trailer-song "$song"
    ;;
  stills)
    rm -rf "$out/stills"
    "$GODOT" --path . --resolution 1280x720 -- "${game[@]}" --trailer-stills "$out/stills" | tee "$out/stills.log" | grep '\[trailer\]'
    echo "stills in $out/stills"
    ;;
  render|cut)
    mkdir -p "$out/shots"
    if [ "$mode" = render ]; then
      # all: a fresh film; a few shots: only those are filmed again, the rest kept for the cut
      if [ "$shots" = all ]; then rm -f "$out"/shots/shot*; fi
      # --fixed-fps: one frame is one 30th of game time, however slowly a frame renders
      "$GODOT" --path . --resolution 1280x720 --fixed-fps 30 -- "${game[@]}" --trailer-record "$out/shots"         | tee "$out/render.log" | grep '\[trailer\]' || true
    fi
    ls "$out"/shots/shot*.mp4 > /dev/null
    # the song from where the first shot of the cut starts (the director writes shotNN.start)
    first=$(ls "$out"/shots/shot*.mp4 | head -1)
    start=$(cat "${first%.mp4}.start" 2>/dev/null || echo 0)
    : > "$out/shots/list.txt"
    for f in "$out"/shots/shot*.mp4; do echo "file '$(basename "$f")'" >> "$out/shots/list.txt"; done
    ffmpeg -y -loglevel error -f concat -safe 0 -i "$out/shots/list.txt" -ss "$start" -i "$song"       -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart "$out/trailer.mp4"
    echo "wrote $out/trailer.mp4 ($(ls "$out"/shots/shot*.mp4 | wc -l) shots, the song from ${start} s)"
    ;;
  *)
    echo "usage: tools/trailer.sh preview|stills|render [shots] | cut" >&2
    exit 2
    ;;
esac
