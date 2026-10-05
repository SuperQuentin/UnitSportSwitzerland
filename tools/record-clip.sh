#!/usr/bin/env bash
# Records a feature clip in one launch (#487): a GIF (and an MP4 when ffmpeg has libx264) of whatever
# the game flags make happen, usually a probe that plays the feature (docs/notes/general/feature-clips.md).
#
#   tools/record-clip.sh <name> <delay> <seconds> [game flags...]
#   e.g. tools/record-clip.sh airstairs 8 12 --stairscheck --world fixture --traffic 0 --airliner arcade
#
# Waits <delay> s after boot, films <seconds> s, writes test_output/clips/<name>.gif (+ .mp4, .log), quits.
# RELEASE=1 also copies the GIF into test_output/release/clips/, where tools/release.sh shows it in the
# next release's notes; CAPTION="..." writes its caption beside it (else the name, dashes as spaces).
# Env: GODOT (default godot), SIZE=WxH window (default 1280x720). Run from the checkout to film.
# A window is needed (nothing is drawn headless); keep it uncovered, macOS skips drawing a covered one.
set -euo pipefail
[ $# -ge 3 ] || { sed -n '2,12p' "$0"; exit 1; }
name=$1 delay=$2 seconds=$3; shift 3
d=test_output/clips; mkdir -p "$d"
ontop=(); [[ ${OSTYPE:-} == darwin* ]] && ontop=(--always-on-top)
"${GODOT:-godot}" "${ontop[@]}" --resolution "${SIZE:-1280x720}" --path . -- \
    --clip "$name,$delay,$seconds" --clip-quit --nocapture "$@" > "$d/$name.log" 2>&1 || echo "exit $?"
grep -E '\[clip\]' "$d/$name.log" || { echo "no [clip] line: see $d/$name.log"; exit 1; }
[ -s "$d/$name.gif" ] || exit 1
if [ "${RELEASE:-0}" = 1 ]; then
  r=test_output/release/clips; mkdir -p "$r"
  cp "$d/$name.gif" "$r/"
  [ -n "${CAPTION:-}" ] && printf '%s\n' "$CAPTION" > "$r/$name.txt"
  echo "staged $r/$name.gif for the next release"
fi
