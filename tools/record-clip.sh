#!/usr/bin/env bash
# Records a feature clip in one launch (#487): a GIF (and an MP4 when ffmpeg has libx264) of whatever
# the game flags make happen, usually a probe that plays the feature (docs/notes/general/feature-clips.md).
#
#   tools/record-clip.sh <name> <delay> <seconds> [game flags...]
#   e.g. tools/record-clip.sh airstairs 8 12 --stairscheck --world fixture --traffic 0 --airliner arcade
#   tools/record-clip.sh trim <in.mp4> <start> <seconds> <name>    a GIF of a part of a recorded MP4
#
# Waits <delay> s after boot, films <seconds> s, writes test_output/clips/<name>.gif (+ .mp4, .log), quits.
# RELEASE=1 also copies the GIF into test_output/release/clips/, where tools/release.sh shows it in the
# next release's notes; CAPTION="..." writes its caption beside it (else the name, dashes as spaces).
# Env: GODOT (default godot), SIZE=WxH window (default 1280x720). Run from the checkout to film.
# A window is needed (nothing is drawn headless); keep it uncovered, macOS skips drawing a covered one.
set -euo pipefail
[ $# -ge 3 ] || { sed -n '2,13p' "$0"; exit 1; }
d=test_output/clips; mkdir -p "$d"
if [ "$1" = trim ]; then
  [ $# -eq 5 ] || { sed -n '7p' "$0"; exit 1; }
  name=$5
  # the GIF settings of Core/ClipRecorder: 560 px, 12 fps, 128 colours, bayer dither
  ffmpeg -hide_banner -loglevel error -y -ss "$3" -t "$4" -i "$2" -filter_complex \
    "fps=12,scale=560:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff:max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" \
    -loop 0 "$d/$name.gif"
  echo "[clip] wrote $d/$name.gif ($(( $(wc -c < "$d/$name.gif") / 1024 )) KB)"
else
  name=$1 delay=$2 seconds=$3; shift 3
  ontop=(); [[ ${OSTYPE:-} == darwin* ]] && ontop=(--always-on-top)
  "${GODOT:-godot}" "${ontop[@]}" --resolution "${SIZE:-1280x720}" --path . -- \
      --clip "$name,$delay,$seconds" --clip-quit --nocapture "$@" > "$d/$name.log" 2>&1 || echo "exit $?"
  # a probe that quits first still leaves whole files: ffmpeg finishes them when the pipe closes
  grep -E '\[clip\]' "$d/$name.log" || { echo "no [clip] line: see $d/$name.log"; exit 1; }
fi
[ -s "$d/$name.gif" ] || exit 1
if [ "${RELEASE:-0}" = 1 ]; then
  r=test_output/release/clips; mkdir -p "$r"
  cp "$d/$name.gif" "$r/"
  if [ -n "${CAPTION:-}" ]; then printf '%s\n' "$CAPTION" > "$r/$name.txt"; fi
  echo "staged $r/$name.gif for the next release"
fi
