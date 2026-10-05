#!/usr/bin/env bash
# Local release: next semver from commits since the last tag, Windows + Linux + macOS exports, GitHub release,
# then the delta files from the previous release (tools/deltas.sh).
# Usage: tools/release.sh [--dry-run]   (Git Bash or WSL, on a main in sync with origin/main; GODOT=godot on WSL)
# The build runs in a temporary worktree of the released commit, so your working files are never touched.
# Needs: gh (logged in), dotnet, Godot mono + export templates (windows, linux, macos), export_presets.cfg in the repo root
# ("Linux" and "macOS" presets are added when missing), curl, unzip, tar, xz, zip or PowerShell.
set -euo pipefail
cd "$(dirname "$0")/.."
DRY=0; [ "${1:-}" = "--dry-run" ] && DRY=1
GODOT=${GODOT:-'/c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.1-stable_mono_win64/godot_v4.7.1-stable_mono_win64_console.exe'}
OUT=test_output/release; mkdir -p "$OUT"

[ "$(git branch --show-current)" = main ] || { echo "Not on main"; exit 1; }
git fetch -q origin --tags
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "main is not in sync with origin/main (push or pull first)"; exit 1; }
SHA=$(git rev-parse HEAD)

last=$(git describe --tags --abbrev=0 "$SHA" --match 'v[0-9]*' 2>/dev/null || true)
if [ -n "$last" ]; then range="$last..$SHA"; cur="${last#v}"; else range="$SHA"; cur="0.0.0"; fi
IFS=. read -r MA MI PA <<< "$cur"

# major: BREAKING in any commit; minor: anything not fix/docs/chore/merge; patch: only fixes/refactors; none: docs/chore only
git log --no-merges --format='%s' $range > "$OUT/subjects.txt"
if git log --no-merges --format='%B' $range | grep -qE 'BREAKING'; then bump=major
elif grep -qvE '^:(bug|ambulance|recycle|art|white_check_mark|memo|wrench|twisted_rightwards_arrows):' "$OUT/subjects.txt"; then bump=minor
elif grep -qE '^:(bug|ambulance|recycle|art|white_check_mark):' "$OUT/subjects.txt"; then bump=patch
else echo "Nothing releasable since ${last:-the start} (docs/chore only)."; exit 0; fi
case $bump in
  major) MA=$((MA+1)); MI=0; PA=0 ;;
  minor) MI=$((MI+1)); PA=0 ;;
  patch) PA=$((PA+1)) ;;
esac
V="$MA.$MI.$PA"

section() { # title, ERE on the leading emoji code
  local body; body=$(git log --no-merges --format='%s' $range | grep -E "$2" | sed -E 's/^:[a-z0-9_+-]+: *//; s/^/- /') || true
  if [ -n "$body" ]; then printf '### %s\n%s\n\n' "$1" "$body"; fi
}
{
  f=$(git log --no-merges --format='%s' $range | grep -vE '^:(bug|ambulance|memo|wrench|recycle|art|white_check_mark|boom):' | sed -E 's/^:[a-z0-9_+-]+: *//; s/^/- /') || true
  if [ -n "$f" ]; then printf '### Features and changes\n%s\n\n' "$f"; fi
  section "Fixes" '^:(bug|ambulance):'
  section "Docs and maintenance" '^:(memo|wrench|recycle|art|white_check_mark):'
  if [ -n "$last" ]; then echo "**Full diff:** $(git remote get-url origin | sed -E 's#git@github.com:#https://github.com/#; s#\.git$##')/compare/$last...v$V"; fi
} > "$OUT/notes.md"

echo "Next version: v$V ($bump)"; cat "$OUT/notes.md"
[ $DRY = 1 ] && exit 0

WT="$PWD/$OUT/wt"; REPO=$PWD
git worktree remove --force "$WT" 2>/dev/null || true
git worktree add -q --detach "$WT" "$SHA"
trap 'cd "$REPO"; git worktree remove --force "$WT"' EXIT
# export_presets.cfg is gitignored (per machine), so the worktree does not have it
[ -f export_presets.cfg ] || { echo "No export_presets.cfg in the repo root"; exit 1; }
cp export_presets.cfg "$WT/"
cd "$WT"
sed -i "s|^config/name=.*|&\\nconfig/version=\"$V\"|" project.godot

ensure_preset() { # name, platform, extra option lines: appended to the (gitignored, per machine) export_presets.cfg when missing
  local f="$REPO/export_presets.cfg" n excl
  grep -q "^name=\"$1\"" "$f" && return
  n=$(grep -c '^\[preset\.[0-9]*\]$' "$f"); excl=$(sed -n 's/^exclude_filter="\(.*\)"$/\1/p' "$f" | head -1)
  echo "Adding preset.$n \"$1\" to export_presets.cfg"
  printf '\n[preset.%s]\n\nname="%s"\nplatform="%s"\nrunnable=false\ndedicated_server=false\ncustom_features=""\nexport_filter="all_resources"\ninclude_filter=""\nexclude_filter="%s"\nexport_path=""\npatches=PackedStringArray()\nencryption_include_filters=""\nencryption_exclude_filters=""\nseed=0\nencrypt_pck=false\nencrypt_directory=false\nscript_export_mode=2\n\n[preset.%s.options]\n\ncustom_template/debug=""\ncustom_template/release=""\ndotnet/include_scripts_content=false\ndotnet/include_debug_symbols=false\ndotnet/embed_build_outputs=false\n%s\n' \
    "$n" "$1" "$2" "$excl" "$n" "$3" >> "$f"
}
ensure_preset "Linux" "Linux" 'binary_format/embed_pck=false
binary_format/architecture="x86_64"
texture_format/s3tc_bptc=true
texture_format/etc2_astc=false'
ensure_preset "macOS" "macOS" 'binary_format/architecture="universal"
texture_format/s3tc_bptc=true
texture_format/etc2_astc=true
application/bundle_identifier="ch.unitsport.switzerland"
codesign/codesign=1
notarization/notarization=0'
cp "$REPO/export_presets.cfg" .

# yt-dlp + its QuickJS runtime (else 403s) + ffmpeg (CD burning, GPX video export) ship in bin/ beside the
# executable (BundledTools), one set per platform, cached in $OUT/tools/<platform>/ between releases; delete it to refresh
TOOLS="$REPO/$OUT/tools"
YT=https://github.com/yt-dlp/yt-dlp/releases/latest/download QJ=https://github.com/quickjs-ng/quickjs/releases/latest/download
FF=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest
get() { [ -s "$1" ] || curl -fsSL -o "$1" "$2"; }
fetch_tools() { # platform, bin dir
  local t="$TOOLS/$1"; mkdir -p "$t" "$2"
  case $1 in
    windows) # LGPL ffmpeg (BtbN, libvorbis included)
      get "$t/yt-dlp.exe" $YT/yt-dlp.exe; get "$t/qjs.exe" $QJ/qjs-windows-x86_64.exe
      [ -s "$t/ffmpeg.exe" ] || { get "$t/ffmpeg.zip" $FF/ffmpeg-master-latest-win64-lgpl.zip; unzip -qjo "$t/ffmpeg.zip" '*/bin/ffmpeg.exe' -d "$t"; }
      cp "$t/yt-dlp.exe" "$t/qjs.exe" "$t/ffmpeg.exe" "$2/" ;;
    linux) # x86_64, LGPL ffmpeg (BtbN)
      get "$t/yt-dlp" $YT/yt-dlp_linux; get "$t/qjs" $QJ/qjs-linux-x86_64
      [ -s "$t/ffmpeg" ] || { get "$t/ffmpeg.tar.xz" $FF/ffmpeg-master-latest-linux64-lgpl.tar.xz; tar -xJf "$t/ffmpeg.tar.xz" -C "$t" --wildcards --strip-components=2 '*/bin/ffmpeg'; }
      cp "$t/yt-dlp" "$t/qjs" "$t/ffmpeg" "$2/" ;;
    macos) # yt-dlp universal; qjs and ffmpeg (martin-riedl.de, GPL, libvorbis) Apple Silicon only: Intel Macs use PATH
      get "$t/yt-dlp" $YT/yt-dlp_macos; get "$t/qjs" $QJ/qjs-darwin-arm64
      [ -s "$t/ffmpeg" ] || { get "$t/ffmpeg.zip" https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip; unzip -qjo "$t/ffmpeg.zip" ffmpeg -d "$t"; }
      cp "$t/yt-dlp" "$t/qjs" "$t/ffmpeg" "$2/" ;;
  esac
}
# tar.gz with Unix modes set explicitly (NTFS has none): $1 archive, $2 dir, then the paths to mark executable
tarball() {
  local out=$1 dir=$2; shift 2
  local all; all=$(cd "$dir" && ls -A); printf '%s\n' "$@" > "$dir.exec"
  tar -cf "${out%.gz}" -C "$dir" --owner=0 --group=0 --mode=a+rX,u+w --exclude-from="$dir.exec" $all
  tar -rf "${out%.gz}" -C "$dir" --owner=0 --group=0 --mode=0755 "$@"
  gzip -f "${out%.gz}"
}
export_preset() { # preset, output file
  "$GODOT" --headless --path . --export-release "$1" "$2"
  [ -e "$2" ] || { echo "Export \"$1\" failed"; exit 1; }
}

dotnet build UnitSportSwitzerland.csproj -c Release
"$GODOT" --headless --path . --import || true
rm -rf build; mkdir -p build/windows build/linux build/macos
ASSETS=()

export_preset "Windows Desktop" build/windows/UnitSportSwitzerland.exe
fetch_tools windows build/windows/bin
ZIP="$REPO/$OUT/UnitSportSwitzerland-v$V-windows.zip"; rm -f "$ZIP"
if command -v zip >/dev/null; then (cd build/windows && zip -qr "$ZIP" .)
else powershell -NoProfile -Command "Compress-Archive -Path 'build/windows/*' -DestinationPath '$(cygpath -w "$ZIP" 2>/dev/null || echo "$ZIP")'"; fi
ASSETS+=("$ZIP")

export_preset "Linux" build/linux/UnitSportSwitzerland.x86_64
fetch_tools linux build/linux/bin
TGZ="$REPO/$OUT/UnitSportSwitzerland-v$V-linux-x86_64.tar.gz"
tarball "$TGZ" build/linux UnitSportSwitzerland.x86_64 bin/yt-dlp bin/qjs bin/ffmpeg
ASSETS+=("$TGZ")

# Godot can only write a macOS export as a .zip off a Mac; unpacked here so the tools go inside the bundle
# (Contents/MacOS/bin, next to the executable) and the exec bits are set. Unsigned: first launch needs xattr -cr.
export_preset "macOS" build/macos.zip
unzip -qo build/macos.zip -d build/macos
APP=$(cd build/macos && ls -d *.app | head -1)
fetch_tools macos "build/macos/$APP/Contents/MacOS/bin"
TGZ="$REPO/$OUT/UnitSportSwitzerland-v$V-macos.tar.gz"
tarball "$TGZ" build/macos $(cd build/macos && find "$APP/Contents/MacOS" -type f)
ASSETS+=("$TGZ")

gh release create "v$V" "${ASSETS[@]}" --target "$SHA" --title "v$V" --notes-file "$REPO/$OUT/notes.md"
echo "Released v$V"

# delta updates (#532): the game updates from $last with these instead of the full archive
if [ -n "$last" ]; then
  (cd "$REPO" && tools/deltas.sh "$last" "v$V" --upload) || echo "Deltas failed; the release stands. Retry with: tools/deltas.sh $last v$V --upload"
fi
