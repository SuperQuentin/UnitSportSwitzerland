#!/usr/bin/env bash
# The status page's pictures and clips (#740), cut from the trailer renders (tools/trailer.sh, docs/notes/trailer/).
# Usage: tools/web-media.sh [trailer dir] [out dir]   (defaults: test_output/trailer, test_output/web-media)
# Never committed: the trailer carries its music (CC BY, credited on the page). tools/deploy-linux.sh uploads the
# out dir to the server's web/media/ when it exists; the page works without it (a drawn alpenglow instead).
# Needs ffmpeg. The shot numbers below are picks from the v1 renders: re-pick them when a film is re-shot.
set -euo pipefail
cd "$(dirname "$0")/.."
SRC=${1:-test_output/trailer} OUT=${2:-test_output/web-media}
command -v ffmpeg >/dev/null || { echo "ffmpeg not found"; exit 1; }
mkdir -p "$OUT"
shot() { echo "$SRC/$1/v1/shots/shot$2.mp4"; }
for f in "$(shot story 23)" "$SRC/story/v1/trailer_share.mp4"; do [ -f "$f" ] || { echo "missing $f: render the trailers first (tools/trailer.sh)"; exit 1; }; done
enc=(-an -c:v libx264 -pix_fmt yuv420p -preset slow -movflags +faststart)

# hero: one muted loop, each clip crossfading into the next (name:shot:seconds used)
HERO=(story:23:1.9 story:28:1.9 story:29:3.6 showcase:16:3.6 story:12:1.9 showcase:13:3.6 showcase:30:1.9)
XF=0.6 inputs=() chain="" t=0 i=0 prev=""
for h in "${HERO[@]}"; do
  IFS=: read -r film n len <<<"$h"
  inputs+=(-t "$len" -i "$(shot "$film" "$n")")
  chain+="[$i:v]scale=1280:720,fps=30,setsar=1,format=yuv420p[v$i];"
  if [ $i = 0 ]; then prev=v0; t=$len
  else
    off=$(awk -v t="$t" -v x=$XF 'BEGIN{printf "%.3f", t-x}')
    chain+="[$prev][v$i]xfade=transition=fade:duration=$XF:offset=$off[x$i];"
    prev=x$i; t=$(awk -v t="$t" -v l="$len" -v x=$XF 'BEGIN{printf "%.3f", t+l-x}')
  fi
  i=$((i + 1))
done
echo "hero.mp4 (${t}s)"
ffmpeg -loglevel error -y "${inputs[@]}" -filter_complex "${chain%;}" -map "[$prev]" -crf 30 "${enc[@]}" "$OUT/hero.mp4"
ffmpeg -loglevel error -y -ss 0.5 -i "$(shot story 28)" -frames:v 1 -vf scale=1280:720 -q:v 4 "$OUT/hero.jpg"

# one short muted loop + its poster per feature row
FEATURES=(real:story:12 drive:showcase:09 fly:showcase:16 water:showcase:11 snow:story:17 together:story:35)
for f in "${FEATURES[@]}"; do
  IFS=: read -r name film n <<<"$f"
  echo "$name.mp4"
  ffmpeg -loglevel error -y -i "$(shot "$film" "$n")" -vf "scale=960:540,fps=30" -crf 28 "${enc[@]}" "$OUT/$name.mp4"
  ffmpeg -loglevel error -y -ss 0.8 -i "$(shot "$film" "$n")" -frames:v 1 -vf scale=960:540 -q:v 4 "$OUT/$name.jpg"
done

# the story trailer, with its music, played on click
echo "trailer.mp4"
ffmpeg -loglevel error -y -i "$SRC/story/v1/trailer_share.mp4" -vf scale=1280:720 -c:v libx264 -crf 25 -preset slow \
  -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart "$OUT/trailer.mp4"
ffmpeg -loglevel error -y -ss 75.5 -i "$SRC/story/v1/trailer_share.mp4" -frames:v 1 -vf scale=1280:720 -q:v 4 "$OUT/trailer.jpg"
du -sh "$OUT"
