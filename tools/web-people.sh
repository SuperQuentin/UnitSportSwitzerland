#!/usr/bin/env bash
# The status page's people (#998): figures from the model viewer, each in one of the four visual styles, cut
# out on a transparent background (--modelsclear) and written as media/people-<n>.webp beside tools/web-media.sh's
# clips. Never committed (media/ is generated); tools/deploy-linux.sh uploads the out dir with the rest.
# Usage: tools/web-people.sh [out dir]   (default test_output/web-media). Windowed: headless renders nothing.
# Needs a built checkout, Godot (GODOT, default godot) and ffmpeg with libwebp. One short launch per figure.
# The order is the page's (index.html's PEOPLE list): keep both in step when the cast changes.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=${1:-test_output/web-media}
GODOT=${GODOT:-godot}
command -v ffmpeg >/dev/null || { echo "ffmpeg not found"; exit 1; }
tmp=test_output/web-people
rm -rf "$tmp"; mkdir -p "$OUT" "$tmp"

# style:filter:png, the filter --modelsonly's text (a category or a name, any case), the png the viewer's file name
# for the figure ('Category / Name' slugged); four of each style
CAST=(
  ps1:ymca:emotes-ymca                   cartoon:galaxy:clothes-galaxy_dress       real-:lolita:clothes-pink_lolita_dress
  real+:gothic:clothes-black_gothic_robe cartoon:cheer:emotes-cheer                ps1:hard:clothes-hard_hat
  real+:walking:figures-walking          real-:selfie:figures-holding___phoneselfie real-:step:emotes-t_step
  real+:"doll skirt":clothes-postal_doll_skirt  ps1:schuss:figures-snow_poses___ski___schuss  cartoon:zib:aliens-zib_waving
  real+:striped:clothes-striped_long_sleeve     cartoon:stance:figures-snow_poses___board___stance
  real-:"taz 90":clothes-taz_90_jacket   ps1:zorp:aliens-zorp_doing_the_robot
)
n=0
for c in "${CAST[@]}"; do
  IFS=: read -r style filter entry <<<"$c"
  engine=(); [ "$style" = real+ ] && engine=(--rendering-method forward_plus)
  dir="$tmp/$n"
  log=$("$GODOT" --path . --resolution 960x1440 ${engine[@]+"${engine[@]}"} -- --models,"$dir" --modelsonly "$filter" --modelsclear     --modelsyaw 160 --modelspitch -6 --style "$style" 2>&1 || true)
  grep -q 'RESULT: ok' <<<"$log" || { echo "viewer failed for $c"; exit 1; }
  png=$(find "$dir" -name "*-$entry.png" | head -1)
  [ -n "$png" ] || { echo "no viewer entry $entry for '$filter' (in $dir)"; exit 1; }
  ffmpeg -loglevel error -y -i "$png" -vf "scale=-2:'min(560,ih)'" -c:v libwebp -lossless 0 -q:v 82 "$OUT/people-$n.webp"
  echo "people-$n.webp  $style  $entry"
  n=$((n + 1))
done
rm -rf "$tmp"
du -ch "$OUT"/people-*.webp | tail -1
