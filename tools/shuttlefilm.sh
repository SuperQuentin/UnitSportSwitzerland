#!/usr/bin/env bash
# The space shuttle's mission at Geneva, filmed (#1497, src/Player/ShuttleFilm): a window (silent, the mouse left
# free; it takes the screen for the run), Geneva's tiles and the globe on D:. Four clips (taxi, take-off and the
# way into space; orbit and retro burn; re-entry; the way down to the landing) cut into one MP4, captioned (also at
# the frames the film marks: the drive's start, the landing's): OUT/shuttle-film.mp4.
# The chase camera (--view third; the cockpit sees nothing in space), a summer morning (DATE, HOUR: no snow).
# The sound: Godot's movie mode (--write-movie) mixes the audio in step with each frame, so the run's whole AVI
# (MOVIE, a few GB of MJPEG, deleted after unless KEEP=1) holds it in time; each clip gets its slice, from the
# frame ClipRecorder logs it began on.
#   GODOT=<console exe> CHUNKS=<tiles with Geneva> [SIZE=1280x720] [OUT=test_output/clips] tools/shuttlefilm.sh
#   tools/shuttlefilm.sh --cut-only    cut the clips already filmed again (with the sound while MOVIE is kept)
# Also OUT/shuttle-film-short.mp4: the taxi skipped, the rest sped up, two passes to SHORT_MB (8 by default).
# Needs ffmpeg with libx264 and drawtext (winget's Gyan.FFmpeg); docs/notes/player/shuttle.md.
set -euo pipefail
d=test_output/clips; out=${OUT:-$d}
movie=${MOVIE:-${TMPDIR:-/tmp}/shuttle-film-movie.avi}
mkdir -p "$d" "$out" "$(dirname "$movie")"
if [ "${1:-}" != --cut-only ]; then
  # the taxi's start, 80 m right of 04's centreline (ShuttleFilm), so its tiles are the first in
  "${GODOT:-godot}" --resolution "${SIZE:-1280x720}" --fixed-fps 30 --write-movie "$movie" --path . -- --shuttlefilm \
      --chunks "${CHUNKS:-D:/UnitSportSwitzerland/terrain_chunks}" --at 2496422,1120535 --clip-hq --nocapture --view third --date "${DATE:-2026-06-21}" --time "${HOUR:-11}" \
      > "$d/shuttle-film.log" 2>&1 || echo "exit $?"
  grep -h "\[shuttlefilm\]\|\[clip\]" "$d/shuttle-film.log" || true
fi
# a caption at each clip's start, and one at its mark (the way into space, the landing) where it has one
# (no colons: drawtext reads one as the end of its text)
caption=("Taxi and take-off, Geneva runway 04" "In orbit, then the retro burn" "Re-entry" "Down from space, home to Geneva")
later=("Into space, the cruise drive up to 400 km" "" "" "Runway 22, in over the lake")
mark=(drive "" "" landing)
font="C\:/Windows/Fonts/segoeuib.ttf"
[ -f /c/Windows/Fonts/segoeuib.ttf ] || font="/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
log="$d/shuttle-film.log"

# cut <file> <width> <skip...> -- <speed...> -- <encoder args...>: the four clips, each from <skip> s of it at <speed>x
# (the sound kept at its pitch), captioned, faded, in one MP4
cut() {
  local file=$1 width=$2; shift 2
  local skip=() speed=()
  while [ "$1" != -- ]; do skip+=("$1"); shift; done; shift
  while [ "$1" != -- ]; do speed+=("$1"); shift; done; shift
  local inputs=() filter="" n=0 i=0 sound=0
  for k in 1 2 3 4; do
    local f="$d/shuttle-$k.mp4" ss=${skip[$((k - 1))]} sp=${speed[$((k - 1))]}
    [ -s "$f" ] || { echo "no $f"; continue; }
    local dur len end tempo="" left first at text
    dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f")
    len=$(awk "BEGIN { print ($dur - $ss) / $sp }")
    end=$(awk "BEGIN { print ($len > 1.2 ? $len - 0.5 : 0) }")
    # atempo takes at most 2x a step
    left=$sp; while awk "BEGIN { exit !($left > 2) }"; do tempo+="atempo=2,"; left=$(awk "BEGIN { print $left / 2 }"); done
    tempo+="atempo=$left"
    # the clip's first frame (ClipRecorder's log): where its sound and its marks are counted from
    first=$(grep -ho "shuttle-$k.gif ([0-9]* KB, [0-9]* frames from frame [0-9]*" "$log" 2>/dev/null | tail -1 | grep -o "[0-9]*$" || true)
    text="drawtext=fontfile='$font':text='${caption[$((k - 1))]}':x=w/32:y=h-h/9:fontsize=h/21:fontcolor=white:borderw=3:bordercolor=black@0.6:enable='lt(t,3.5)'"
    if [ -n "${mark[$((k - 1))]}" ] && [ -n "$first" ]; then
      at=$(grep -ho "mark ${mark[$((k - 1))]} at frame [0-9]*" "$log" | tail -1 | grep -o "[0-9]*$" || true)
      if [ -n "$at" ]; then
        at=$(awk "BEGIN { t = (($at - $first) / 30 - $ss) / $sp; print (t > 0 ? t : 0) }")
        text+=",drawtext=fontfile='$font':text='${later[$((k - 1))]}':x=w/32:y=h-h/9:fontsize=h/21:fontcolor=white:borderw=3:bordercolor=black@0.6:enable='between(t,$at,$at+3.5)'"
      fi
    fi
    inputs+=(-i "$f")
    filter+="[$i:v]trim=start=$ss,setpts=(PTS-STARTPTS)/$sp,scale=$width:-2,fps=30,setsar=1,$text,fade=t=in:st=0:d=0.5,fade=t=out:st=$end:d=0.5[v$n];"
    i=$((i + 1))
    # the clip's sound: the movie's audio from the frame its first picture shows (the frame before the one it was read in)
    if [ -n "$first" ] && [ -s "$movie" ]; then
      inputs+=(-ss "$(awk "BEGIN { print ($first - 1) / 30 }")" -t "$dur" -i "$movie"); sound=1
    else
      inputs+=(-f lavfi -t "$dur" -i anullsrc=r=48000:cl=stereo)
    fi
    filter+="[$i:a]aresample=48000,aformat=channel_layouts=stereo,apad,atrim=start=$ss:end=$dur,asetpts=PTS-STARTPTS,$tempo,afade=t=in:st=0:d=0.5,afade=t=out:st=$end:d=0.5[a$n];"
    i=$((i + 1))
    n=$((n + 1))
  done
  [ $n -gt 0 ] || { echo "[shuttlefilm] no clips to cut"; return 1; }
  [ $sound = 1 ] || echo "[shuttlefilm] no movie track ($movie): $file is silent"
  for j in $(seq 0 $((n - 1))); do filter+="[v$j][a$j]"; done
  # the game's mix is quiet (the take-off peaks at -27 dB): brought up to a film's loudness
  filter+="concat=n=$n:v=1:a=1[out][acat];[acat]loudnorm=I=-16:LRA=11:TP=-1.5,aresample=48000[aout]"
  ffmpeg -hide_banner -loglevel error -y "${inputs[@]}" -filter_complex "$filter" -map "[out]" -map "[aout]" \
      -pix_fmt yuv420p -movflags +faststart "$@" "$file"
  echo "[shuttlefilm] wrote $file ($n clips, $(ffprobe -v error -show_entries format=duration -of csv=p=0 "$file") s, $(( $(wc -c < "$file") / 1024 )) KB)"
}

# the whole film
cut "$out/shuttle-film.mp4" 1280 0 0 0 0 -- 1 1 1 1 -- -c:v libx264 -crf 20 -preset slow -c:a aac -b:a 160k
# a short one (SHORT_MB, 8 by default): the taxi skipped, sped up, two passes to the size
mb=${SHORT_MB:-8}
skip=(${SHORT_SKIP:-18 0 0 0}) speed=(${SHORT_SPEED:-3 3 2 5})
total=0
for k in 1 2 3 4; do
  [ -s "$d/shuttle-$k.mp4" ] || continue
  total=$(awk "BEGIN { print $total + ($(ffprobe -v error -show_entries format=duration -of csv=p=0 "$d/shuttle-$k.mp4") - ${skip[$((k - 1))]}) / ${speed[$((k - 1))]} }")
done
# the size (MB of 1,000,000 bytes) less the sound (96 kbit/s) and 6 % for the container and the rate's overshoot, over the length
kbps=$(awk "BEGIN { print int($mb * 8000 * 0.94 / $total - 96) }")
pass="$(dirname "$movie")/shuttle-short-pass"
cut "$out/shuttle-film-short.mp4" 960 "${skip[@]}" -- "${speed[@]}" -- -c:v libx264 -b:v ${kbps}k -preset slow -pass 1 -passlogfile "$pass" -c:a aac -b:a 96k
cut "$out/shuttle-film-short.mp4" 960 "${skip[@]}" -- "${speed[@]}" -- -c:v libx264 -b:v ${kbps}k -preset slow -pass 2 -passlogfile "$pass" -c:a aac -b:a 96k
rm -f "$pass"*
[ "${KEEP:-0}" = 1 ] || [ "${1:-}" = --cut-only ] || rm -f "$movie"
