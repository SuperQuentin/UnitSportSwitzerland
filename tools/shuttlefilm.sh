#!/usr/bin/env bash
# The space shuttle's mission at Geneva, filmed (#1497, src/Player/ShuttleFilm): a window (silent, the mouse left
# free; it takes the screen for the run), Geneva's tiles and the globe on D:. Five clips (taxi and take-off, the
# drive up, orbit and retro burn, re-entry, the landing) cut into one MP4 with a caption on each: OUT/shuttle-film.mp4.
# The chase camera (--view third; the cockpit sees nothing in space), a summer morning (DATE, HOUR: no snow).
#   GODOT=<console exe> CHUNKS=<tiles with Geneva> [SIZE=1280x720] [OUT=test_output/clips] tools/shuttlefilm.sh
#   tools/shuttlefilm.sh --cut-only    cut the clips already filmed again
# Needs ffmpeg with libx264 and drawtext (winget's Gyan.FFmpeg); docs/notes/player/shuttle.md.
set -euo pipefail
d=test_output/clips; out=${OUT:-$d}
mkdir -p "$d" "$out"
if [ "${1:-}" != --cut-only ]; then
  # the taxi's start, 80 m right of 04's centreline (ShuttleFilm), so its tiles are the first in
  "${GODOT:-godot}" --resolution "${SIZE:-1280x720}" --fixed-fps 30 --audio-driver Dummy --path . -- --shuttlefilm \
      --chunks "${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}" --at 2496422,1120535 --clip-hq --nocapture       --view third --date "${DATE:-2026-06-21}" --time "${HOUR:-11}" \
      > "$d/shuttle-film.log" 2>&1 || echo "exit $?"
  grep -h "\[shuttlefilm\]\|\[clip\]" "$d/shuttle-film.log" || true
fi
# (no colons: drawtext reads one as the end of its text)
captions=("Taxi and take-off, Geneva runway 04" "The cruise drive, up to 400 km" "In orbit, then the retro burn" "Re-entry" "Home to runway 22, in over the lake")
font="C\\:/Windows/Fonts/segoeuib.ttf"
[ -f /c/Windows/Fonts/segoeuib.ttf ] || font="/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
inputs=() filter="" n=0
for k in 1 2 3 4 5; do
  f="$d/shuttle-$k.mp4"
  [ -s "$f" ] || { echo "no $f"; continue; }
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")
  end=$(awk "BEGIN { print ($dur > 1.2 ? $dur - 0.5 : 0) }")
  inputs+=(-i "$f")
  filter+="[$n:v]scale=1280:-2,fps=30,setsar=1,drawtext=fontfile='$font':text='${captions[$((k - 1))]}':x=40:y=h-80:fontsize=34:fontcolor=white:borderw=3:bordercolor=black@0.6:enable='lt(t,4)',fade=t=in:st=0:d=0.5,fade=t=out:st=$end:d=0.5[v$n];"
  n=$((n + 1))
done
[ $n -gt 0 ] || { echo "[shuttlefilm] no clips to cut"; exit 1; }
for j in $(seq 0 $((n - 1))); do filter+="[v$j]"; done
filter+="concat=n=$n:v=1:a=0[out]"
ffmpeg -hide_banner -loglevel error -y "${inputs[@]}" -filter_complex "$filter" -map "[out]" \
    -c:v libx264 -crf 20 -preset slow -pix_fmt yuv420p -movflags +faststart "$out/shuttle-film.mp4"
echo "[shuttlefilm] wrote $out/shuttle-film.mp4 ($n clips, $(( $(wc -c < "$out/shuttle-film.mp4") / 1024 / 1024 )) MB)"
