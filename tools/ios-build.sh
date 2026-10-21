#!/usr/bin/env bash
# iOS build (#1025, docs/notes/core/ios-build.md): Godot exports the Xcode project, xcodebuild compiles it, unsigned.
# Usage: tools/ios-build.sh [--export-only] [--simulator]   (on a Mac with Xcode 26, .NET 9 and the Godot 4.7.2 mono export templates)
# Godot refuses to export C# to iOS anywhere but macOS. The "iOS" preset comes from tools/ci/export_presets.cfg
# and is added to the local (gitignored) export_presets.cfg when missing.
# Output in test_output/ios/: UnitSportSwitzerland.xcodeproj, the logs, the .app under dd/ and UnitSportSwitzerland-unsigned.ipa.
set -euo pipefail
EXPORT_ONLY=0; SIMULATOR=0
for a in "$@"; do case $a in
--export-only) EXPORT_ONLY=1 ;; # stops after the Xcode project
--simulator) SIMULATOR=1 ;; # the simulator slices get the game too (two more NativeAOT passes, ~45 min each on CI)
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
# Godot always publishes the simulator slices as well; without --simulator they are empty stubs (UnitSportSwitzerland.csproj)
[ $SIMULATOR = 1 ] || export UNITSPORT_IOS_DEVICE_ONLY=1
"$GODOT" --headless --path . --import > "$OUT/import.log" 2>&1 || true
# The path names the project: <NAME>.xcodeproj and <NAME>/ land beside it
"$GODOT" --headless --verbose --path . --export-release iOS "$OUT/$NAME.ipa" 2>&1 | tee "$OUT/export.log" || true
# Godot keeps the dotnet publish (NativeAOT) log in its own folder: copied beside the others
log=$(ls -t "$HOME/Library/Application Support/Godot/mono/build_logs"/*_ExportRelease/msbuild_log.txt 2>/dev/null | head -1) || true
# A failed publish still leaves an Xcode project (and a "successful" app) with no game library in it
if [ -n "$log" ]; then cp "$log" "$OUT/msbuild.log"
  if grep -qE ': error |error [A-Z]+[0-9]+:' "$OUT/msbuild.log"; then
    echo "dotnet publish failed; full log in $OUT/msbuild.log"
    grep -E ': error |error [A-Z]+[0-9]+:|^\s*Exec: (ld|clang): ' "$OUT/msbuild.log" | grep -v 'ld: warning' | sort -u | cut -c1-400 | head -30; exit 1
  fi
fi
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
# An unsigned .ipa (Payload/<app>): xtool (WSL, Linux) signs it with an Apple ID and installs it, no Mac needed
rm -rf "$OUT/Payload"; mkdir -p "$OUT/Payload"; cp -R "$app" "$OUT/Payload/"
(cd "$OUT" && zip -qry "$NAME-unsigned.ipa" Payload) && rm -rf "$OUT/Payload"
echo "Unsigned ipa: $OUT/$NAME-unsigned.ipa ($(du -sh "$OUT/$NAME-unsigned.ipa" | cut -f1))"
