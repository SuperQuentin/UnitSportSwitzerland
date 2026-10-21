#!/usr/bin/env bash
# iOS build (#1025, docs/notes/core/ios-build.md): Godot exports the Xcode project, xcodebuild compiles it, unsigned.
# Usage: tools/ios-build.sh [--export-only]   (on a Mac with Xcode, .NET 8 and the Godot 4.7.2 mono export templates)
# Godot refuses to export C# to iOS anywhere but macOS. The "iOS" preset comes from tools/ci/export_presets.cfg
# and is added to the local (gitignored) export_presets.cfg when missing.
# Output in test_output/ios/: UnitSportSwitzerland.xcodeproj, export.log, xcodebuild.log, and the .app under dd/.
set -euo pipefail
EXPORT_ONLY=0
for a in "$@"; do case $a in
--export-only) EXPORT_ONLY=1 ;; # stops after the Xcode project
*) echo "Unknown argument $a"; exit 2 ;;
esac; done
[ "$(uname)" = Darwin ] || { echo "iOS export of a C# project needs macOS (Godot: 'requires macOS')"; exit 1; }
cd "$(dirname "$0")/.."
GODOT=${GODOT:-$(command -v godot || echo /Applications/Godot_mono.app/Contents/MacOS/Godot)}
OUT=test_output/ios NAME=UnitSportSwitzerland

# The iOS preset block of the committed CI presets, renumbered after the local ones
if [ ! -f export_presets.cfg ]; then cp tools/ci/export_presets.cfg export_presets.cfg
elif ! grep -q '^name="iOS"$' export_presets.cfg; then
  n=$(grep -c '^\[preset\.[0-9]*\]$' export_presets.cfg)
  echo "Adding preset.$n \"iOS\" to export_presets.cfg"
  awk -v n="$n" '/^\[preset\.[0-9]+\]$/ { keep = 0 } /^name="iOS"$/ { keep = 1; print "\n[preset." n "]\n" }
    /^\[preset\.[0-9]+\.options\]$/ && keep { $0 = "[preset." n ".options]" } keep' tools/ci/export_presets.cfg >> export_presets.cfg
fi

rm -rf "$OUT"; mkdir -p "$OUT"
"$GODOT" --headless --path . --import > "$OUT/import.log" 2>&1 || true
# The path names the project: <NAME>.xcodeproj and <NAME>/ land beside it
"$GODOT" --headless --path . --export-release iOS "$OUT/$NAME.ipa" 2>&1 | tee "$OUT/export.log" || true
[ -d "$OUT/$NAME.xcodeproj" ] || { echo "Godot wrote no Xcode project; see $OUT/export.log"; grep -E 'ERROR|error' "$OUT/export.log" | head -20; exit 1; }
echo "Xcode project: $OUT/$NAME.xcodeproj"
[ $EXPORT_ONLY = 1 ] && exit 0

# Unsigned device build: proves the NativeAOT game library and the template link; installing on a phone needs a team
set +e
xcodebuild -project "$OUT/$NAME.xcodeproj" -scheme "$NAME" -configuration Release -sdk iphoneos \
  -destination 'generic/platform=iOS' -derivedDataPath "$OUT/dd" \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build > "$OUT/xcodebuild.log" 2>&1
rc=$?; set -e
grep -E 'error:|BUILD (SUCCEEDED|FAILED)' "$OUT/xcodebuild.log" | head -30
[ $rc = 0 ] || { echo "xcodebuild failed ($rc); full log in $OUT/xcodebuild.log"; exit $rc; }
app=$(find "$OUT/dd/Build/Products" -maxdepth 2 -name '*.app' -type d | head -1)
echo "Built $app ($(du -sh "$app" | cut -f1))"
