#!/usr/bin/env bash
# The trailer (#706): preview its shots, frame them, or film the whole thing and cut it on its
# song. One game launch per run; the director stages each shot. The films: FILM=story (default,
# docs/trailer/script.md, StoryScript.cs), FILM=showcase (docs/trailer/storyboard.md,
# TrailerScript.cs), FILM=drognens (the music clip, docs/trailer/drognens.md, DrognensScript.cs),
# FILM=fondue ("The Stolen Fondue", docs/trailer/fondue.md, FondueScript.cs, #864),
# FILM=tourist ("The Alien Tourist", docs/trailer/tourist.md, TouristScript.cs, #865) and
# FILM=invasion ("Invasion, Swiss Style", docs/trailer/invasion.md, InvasionScript.cs, #866).
#
#   tools/trailer.sh preview [shots]   play in a window, each shot with its part of the song
#   tools/trailer.sh stills  [shots]   first, middle and last frame of each shot as PNGs (at --fixed-fps 30, as render)
#   tools/trailer.sh render  [shots]   film the shots (1920x1080, 30 fps, frame-exact) and cut them
#                                      on the song, as the film's next version; a few shots are
#                                      filmed again and cut with the last version's others
#   tools/trailer.sh cut               cut the newest version again (after replacing a shot by hand,
#                                      or after new translations)
#   tools/trailer.sh lines             add the film's English lines (captions, bubbles, chat) to its
#                                      translations, docs/trailer/<film>.subtitles.json, and say
#                                      what each language lacks
#
# Subtitles (#862): the director writes each filmed shot's English lines and their frames beside
# it (shotNN.cues.json); the cut writes <film>.<lang>.srt into the version folder for English and
# every language of docs/trailer/<film>.subtitles.json ({"id": {"en", "fr", "de", "it", "rm"}};
# a missing translation is English, with a warning) and muxes them into trailer.mp4 and
# trailer_share.mp4 as soft tracks (mov_text, language and title set, none on by default: the
# bubbles are burnt in, in English).
#
# Every render is a version of its own, never written over: <trailers>/<film>/v1, v2, ... each with
# trailer.mp4, trailer_share.mp4 (lighter, to send), its shots/, render.log and version.txt (when,
# which commit, which shots were filmed). <trailers> is the main checkout's test_output/trailer, so
# the films outlive the worktree they were made in. Stills are scratch, in this checkout's
# test_output/trailer/<film>/stills.
#
# shots: all (default), 5, 5-9 or 5,7,12 (shot numbers of the film).
# Env: GODOT (default godot), CHUNKS (terrain; default ./terrain_chunks, else where this checkout's
# or the main checkout's terrain_location.json points, else the main checkout's terrain_chunks; a
# fixture course as fixture:lake), ARGS (more game arguments, e.g. "--at E,N --trailer-allow-generated"),
# SIZE (default 1920x1080), LOG=1 prints every actor twice a second,
# TRAILERS (where the versions go), VERSION (which one `cut` cuts; default the newest).
# The film's song (incompetech.com, CC BY 4.0: "Voxel Revolution" for the trailers, "I Got a Stick
# Feat James Gavins" for the clip, "Hep Cats" for the fondue film) is downloaded once into <trailers>/ (not committed). Needs ffmpeg
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
  fondue) title="Hep Cats" ;;
  tourist) title="Mighty Like Us" ;;
  invasion) title="Space 1990" ;;
  *) title="Voxel Revolution" ;;
esac
song=$TRAILERS/$(echo "$title" | tr 'A-Z ' 'a-z_').mp3

# the chunks a terrain_location.json points at ({"chunks": "..."}), empty without one
located() {
  [ -f "$1" ] && sed -n 's/.*"chunks"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1" | head -1
}
if [ -z "${CHUNKS:-}" ]; then
  if [ -f terrain_chunks/manifest.json ]; then CHUNKS=terrain_chunks
  elif [ -n "$(located terrain_location.json)" ]; then CHUNKS=$(located terrain_location.json)
  elif [ -n "$(located "$main/terrain_location.json")" ]; then CHUNKS=$(located "$main/terrain_location.json")
  else CHUNKS="$main/terrain_chunks"
  fi
fi
case $CHUNKS in
  fixture:*) ;;
  *) [ -f "$CHUNKS/manifest.json" ] || echo "warning: no manifest.json in $CHUNKS: the director will find no real tiles" >&2 ;;
esac

# the song only for what plays or cuts it
if [ "$mode" != stills ] && [ "$mode" != lines ] && [ ! -f "$song" ]; then
  curl -fsSL -o "$song" "https://incompetech.com/music/royalty-free/mp3-royaltyfree/${title// /%20}.mp3"
fi

log=()
[ "${LOG:-}" = 1 ] && log=(--trailer-log)
# ARGS: more game arguments, split on spaces (ARGS="--at 2600000,1200000 --trailer-allow-generated")
extra=()
[ -n "${ARGS:-}" ] && read -r -a extra <<< "$ARGS"
game=(--chunks "$CHUNKS" --title "trailer $mode $shots" --trailer "$shots" --trailer-film "$FILM" --trailer-size "$SIZE" ${log[@]+"${log[@]}"} ${extra[@]+"${extra[@]}"})

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
  subtitle_tracks "$out"
  ffmpeg -y -loglevel error -i "$out/trailer.mp4" -map 0 -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 160k \
    -c:s copy -movflags +faststart "$out/trailer_share.mp4"
  subtitles_off "$out/trailer.mp4"
  subtitles_off "$out/trailer_share.mp4"
  echo "wrote $out/trailer.mp4 and trailer_share.mp4 ($(ls "$out"/shots/shot*.mp4 | wc -l) shots, the song from ${start} s)"
}

# every subtitle track of MP4 $1 off by default: ffmpeg's mp4 muxer enables the first track of
# each kind when none is default (QuickTime then shows it), so the tkhd "enabled" flag is cleared
# on the text tracks by hand (python3; without it, a warning and the first track stays on)
subtitles_off() {
  [ -f "$1" ] || return 0
  python3 - "$1" <<'PY' || echo "warning: could not turn the subtitle tracks off by default in $1" >&2
import struct, sys
path = sys.argv[1]
data = bytearray(open(path, "rb").read())
def atoms(start, end):
    while start + 8 <= end:
        size, kind = struct.unpack(">I4s", data[start:start + 8])
        if size < 8: return
        yield start, size, kind
        start += size
off = 0
for at, size, kind in atoms(0, len(data)):
    if kind != b"moov": continue
    for t, tsize, tkind in atoms(at + 8, at + size):
        if tkind != b"trak": continue
        tkhd = handler = None
        for c, csize, ckind in atoms(t + 8, t + tsize):
            if ckind == b"tkhd": tkhd = c
            if ckind == b"mdia":
                for m, msize, mkind in atoms(c + 8, c + csize):
                    if mkind == b"hdlr": handler = bytes(data[m + 16:m + 20])
        if tkhd is not None and handler in (b"sbtl", b"text", b"subt"):
            data[tkhd + 11] &= 0xFE
            off += 1
open(path, "wb").write(data)
print(f"subtitle tracks off by default: {off}")
PY
}

# the cut's subtitles (#862): <film>.<lang>.srt from the shots' cues and the translations, muxed
# into version folder $1's trailer.mp4 as soft tracks, none on by default
subtitle_tracks() {
  local out=$1 lang iso title n=0
  local inputs=() meta=() log
  rm -f "$out/$FILM".*.srt
  # the RESULT line decides, not Godot's exit code (docs/notes/general/headless-exit-139.md)
  log=$("$GODOT" --headless --path . -- --trailer-subtitles "$out" --trailer-film "$FILM" 2>&1 || true)
  echo "$log" | grep '\[subtitles\]' || true
  if ! echo "$log" | grep -q 'RESULT: ok'; then
    echo "no subtitles for this cut" >&2
    return 0
  fi
  for lang in en fr de it rm; do
    [ -f "$out/$FILM.$lang.srt" ] || continue
    case $lang in
      en) iso=eng; title=English ;;
      fr) iso=fra; title="Français" ;;
      de) iso=deu; title=Deutsch ;;
      it) iso=ita; title=Italiano ;;
      rm) iso=roh; title=Rumantsch ;;
    esac
    inputs+=(-i "$out/$FILM.$lang.srt")
    meta+=(-map $((n + 1)):s "-metadata:s:s:$n" "language=$iso" "-metadata:s:s:$n" "title=$title"
      "-metadata:s:s:$n" "handler_name=$title" "-disposition:s:$n" 0)
    n=$((n + 1))
  done
  [ "$n" -gt 0 ] || return 0
  ffmpeg -y -loglevel error -i "$out/trailer.mp4" ${inputs[@]+"${inputs[@]}"} -map 0:v -map 0:a \
    ${meta[@]+"${meta[@]}"} -c:v copy -c:a copy -c:s mov_text -movflags +faststart "$out/trailer.subs.mp4"
  mv "$out/trailer.subs.mp4" "$out/trailer.mp4"
  echo "subtitle tracks: $n ($(cd "$out" && ls "$FILM".*.srt | tr '\n' ' '))"
}

case $mode in
  preview)
    # --fixed-fps as the render: under load the actors keep the film's time, not the wall clock's
    "$GODOT" --path . --resolution 1280x720 --fixed-fps 30 -- "${game[@]}" --trailer-song "$song"
    ;;
  stills)
    rm -rf "$scratch/stills"
    # --fixed-fps as the render: a still is the frame at its film time, never one skipped under load
    "$GODOT" --path . --resolution 1280x720 --fixed-fps 30 -- "${game[@]}" --trailer-stills "$scratch/stills" 2>&1 | tee "$scratch/stills.log" | grep '\[trailer\]'
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
  lines)
    "$GODOT" --headless --path . -- --trailer-lines --trailer-film "$FILM" 2>&1 | grep '\[subtitles\]' || true
    ;;
  cut)
    version=${VERSION:-$(newest)}
    [ -d "$films/v$version" ] || { echo "no $FILM version v$version in $films" >&2; exit 1; }
    cut_film "$films/v$version"
    ;;
  *)
    echo "usage: [FILM=story|showcase|drognens|fondue|tourist|invasion|bubbles|ufo] tools/trailer.sh preview|stills|render [shots] | cut | lines" >&2
    exit 2
    ;;
esac
