#!/usr/bin/env bash
# Delta updates (#532): the .delta files from release vA to release vB, one per platform, made from the
# published archives (exactly what players have; vB's are taken from test_output/release/ when the
# release was just built there), then checked by applying each one to a copy of vA and comparing with vB.
# --upload attaches them to vB's GitHub release; the game then updates vA -> vB with them.
# Usage: tools/deltas.sh vA vB [--upload]      (Git Bash, WSL, Linux or macOS; needs gh, dotnet, unzip, tar)
set -euo pipefail
cd "$(dirname "$0")/.."
[ $# -ge 2 ] || { echo "usage: tools/deltas.sh vA vB [--upload]"; exit 2; }
A=$1 B=$2 UPLOAD=0; [ "${3:-}" = "--upload" ] && UPLOAD=1
OUT=test_output/release/deltas; mkdir -p "$OUT"
dotnet build tools/DeltaGen -c Release -o "$OUT/deltagen" > "$OUT/deltagen-build.log" || { cat "$OUT/deltagen-build.log"; exit 1; }
DG() { dotnet "$OUT/deltagen/DeltaGen.dll" "$@"; }

unpack() { # tag, asset suffix -> prints the unpacked folder
  local name="UnitSportSwitzerland-$1-$2" dir="$OUT/$1/$2" src
  if [ ! -d "$dir" ]; then
    src=test_output/release/$name
    if [ ! -s "$src" ]; then gh release download "$1" -p "$name" -D "$OUT/$1" --clobber >&2; src=$OUT/$1/$name; fi
    mkdir -p "$dir"
    case $name in *.zip) unzip -qo "$src" -d "$dir" >&2 ;; *) tar -xzf "$src" -C "$dir" ;; esac
  fi
  echo "$dir"
}

made=()
for p in windows:windows.zip linux-x86_64:linux-x86_64.tar.gz macos:macos.tar.gz; do
  plat=${p%%:*} suffix=${p#*:}
  old=$(unpack "$A" "$suffix") new=$(unpack "$B" "$suffix")
  exec=()
  case $plat in
    linux-x86_64) exec=(UnitSportSwitzerland.x86_64 bin/yt-dlp bin/qjs bin/ffmpeg) ;;
    macos) # the delta is relative to the .app: that is the install root the game patches
      old=$(ls -d "$old"/*.app | head -1) new=$(ls -d "$new"/*.app | head -1)
      while IFS= read -r f; do exec+=("$f"); done < <(cd "$new" && find Contents/MacOS -type f) ;;
  esac
  flags=(); for f in "${exec[@]}"; do flags+=(--exec "$f"); done
  delta="$OUT/UnitSportSwitzerland-$A-to-$B-$plat.delta"
  DG make --from "$A" --to "$B" --old "$old" --new "$new" --out "$delta" "${flags[@]}" | tail -1

  # the proof: apply it to a copy of vA (the game's own staging and swap script) and compare with vB
  rm -rf "$OUT/check"; cp -r "$old" "$OUT/check"
  DG apply "$OUT/check" "$A" "$B" "$delta" > /dev/null
  diff -rq "$OUT/check" "$new" > /dev/null || { echo "$plat: applying the delta does not give $B"; exit 1; }
  rm -rf "$OUT/check"
  made+=("$delta")
done

if [ $UPLOAD = 1 ]; then
  gh release upload "$B" "${made[@]}" --clobber
  echo "Uploaded ${#made[@]} deltas to $B"
else
  printf '%s\n' "${made[@]}"
fi
