#!/usr/bin/env bash
# The status page's pictures and clips (#740), cut from the trailer renders (tools/trailer.sh, docs/notes/trailer/).
# Usage: tools/web-media.sh [trailer dir] [out dir]   (defaults: test_output/trailer, test_output/web-media)
# Never committed: the trailers carry their music (CC BY, credited on the page). tools/deploy-linux.sh uploads the
# out dir to the server's web/media/ when it exists; the page works without it (a drawn alpenglow instead).
# The people beside them come from tools/web-people.sh (#998).
# Needs ffmpeg. Each film's newest version is cut (VERSION_story=v2, VERSION_showcase=v3 pin one). The shot numbers
# below are picks from story v2 and showcase v2 (#998): re-pick them when a film is re-shot.
# A row whose clip is not in a trailer takes test_output/clips/web-<name>.mp4 (tools/record-clip.sh) when it is there.
set -euo pipefail
cd "$(dirname "$0")/.."
SRC=${1:-test_output/trailer} OUT=${2:-test_output/web-media}
command -v ffmpeg >/dev/null || { echo "ffmpeg not found"; exit 1; }
mkdir -p "$OUT"
# the newest vN of a film, or VERSION_<film>
version() { local pin="VERSION_$1"; [ -n "${!pin:-}" ] && { echo "${!pin}"; return; }
  ls -d "$SRC/$1"/v[0-9]* 2>/dev/null | sed 's|.*/v||' | sort -n | tail -1 | sed 's/^/v/'; }
STORY=$(version story) SHOWCASE=$(version showcase)
[ -n "$STORY" ] && [ -n "$SHOWCASE" ] || { echo "no story or showcase render in $SRC: render the trailers first (tools/trailer.sh)"; exit 1; }
echo "story $STORY, showcase $SHOWCASE"
shot() { local v=$STORY; [ "$1" = showcase ] && v=$SHOWCASE; echo "$SRC/$1/$v/shots/shot$2.mp4"; }
for f in "$(shot story 23)" "$SRC/story/$STORY/trailer_share.mp4" "$SRC/showcase/$SHOWCASE/trailer_share.mp4"; do
  [ -f "$f" ] || { echo "missing $f: render the trailers first (tools/trailer.sh)"; exit 1; }; done
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

# one short muted loop + its poster per feature row (index.html's FEATURES): name:film:shot, or name:clip for a recorded one
FEATURES=(real:story:12 drive:showcase:09 fly:showcase:16 water:showcase:11 snow:showcase:08 build:showcase:10
  concert:clip battle:showcase:24 studio:story:35 together:story:35)
for f in "${FEATURES[@]}"; do
  IFS=: read -r name film n <<<"$f"
  if [ "$film" = clip ]; then
    src=test_output/clips/web-$name.mp4
    [ -f "$src" ] || { echo "$name: no $src, the row shows no picture"; continue; }
  else src=$(shot "$film" "$n"); fi
  echo "$name.mp4"
  ffmpeg -loglevel error -y -t 8 -i "$src" -vf "scale=960:540,fps=30" -crf 28 "${enc[@]}" "$OUT/$name.mp4"
  ffmpeg -loglevel error -y -ss 0.8 -i "$src" -frames:v 1 -vf scale=960:540 -q:v 4 "$OUT/$name.jpg"
done

# the two trailers, with their music, played on click: the story (trailer.mp4) and the showcase
trailer() { # film, out name, poster second
  echo "$2.mp4"
  local src="$SRC/$1/$(version "$1")/trailer_share.mp4"
  ffmpeg -loglevel error -y -i "$src" -map 0:v:0 -map 0:a:0 -vf scale=1280:720 -c:v libx264 -crf 25 -preset slow \
    -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart "$OUT/$2.mp4"
  ffmpeg -loglevel error -y -ss "$3" -i "$src" -frames:v 1 -vf scale=1280:720 -q:v 4 "$OUT/$2.jpg"
}
trailer story trailer 75.5
trailer showcase showcase 47.5
du -sh "$OUT"
