#!/usr/bin/env bash
# APK size check (#1198, docs/notes/general/local-release.md "Size"): what the Android export would pack, without
# Godot or the Android SDK. Runs the .NET publish Godot's export runs (GodotTools BuildSystem: ExportRelease,
# android-arm64, self-contained, GodotTargetPlatform=android) into a temp folder, then fails on
#   - a file a phone never loads from assets/ (an .aar, a static .a, a macOS .dylib, a native Windows .dll:
#     a .dll without the CLI metadata signature "BSJB"), or one DropUnusedAndroidNatives keeps out (SDL, the
#     mono debugger, hot-reload and tracing components);
#   - a publish over APKCHECK_BUDGET_MB (80; 65 MB unpacked in Oct 2026, ONNX's .aar added 70);
#   - an Android preset that does not compress the native libs (the engine, 71 MB stored, 24 MB packed).
# The real APK is measured by tools/release.sh after the export (APK_BUDGET_MB, 100). ~80 s, most of it the build.
set -u
cd "$(dirname "$0")/.."
BUDGET=${APKCHECK_BUDGET_MB:-80}
OUTDIR=$(mktemp -d)   # never under the project (a build output there broke the game once)
trap 'rm -rf "$OUTDIR"' EXIT
fail=()

dotnet publish UnitSportSwitzerland.csproj -c ExportRelease -r android-arm64 --self-contained true \
  -p:GodotTargetPlatform=android -o "$OUTDIR" -v quiet -nologo -clp:ErrorsOnly
code=$?
if [ $code != 0 ] || [ ! -f "$OUTDIR/UnitSportSwitzerland.dll" ]; then
  echo "[apksizecheck] RESULT: FAILED the Android publish failed (exit $code)"; exit 1
fi

for f in "$OUTDIR"/*; do
  n=$(basename "$f")
  case $n in
    *.aar | *.a | *.dylib) fail+=("$n: never loaded on a phone") ;;
    libSDL3.so | libmono-component-debugger.so | libmono-component-hot_reload.so | libmono-component-diagnostics_tracing.so)
      fail+=("$n: DropUnusedAndroidNatives keeps it out") ;;
    *.dll) grep -aq BSJB "$f" || fail+=("$n: a native Windows library") ;;
  esac
done

bytes=$(du -sb "$OUTDIR" | cut -f1)
mb=$((bytes / 1000000))
echo "[apksizecheck] publish: $mb MB in $(ls "$OUTDIR" | wc -l) files (budget $BUDGET MB); biggest:"
ls -S "$OUTDIR" | head -5 | while read -r n; do echo "  $(( $(stat -c%s "$OUTDIR/$n") / 1000 )) kB  $n"; done
[ "$mb" -gt "$BUDGET" ] && fail+=("the publish is $mb MB, over the $BUDGET MB budget")

grep -q '^gradle_build/compress_native_libraries=true' tools/ci/export_presets.cfg \
  || fail+=("tools/ci/export_presets.cfg: the Android preset does not set gradle_build/compress_native_libraries=true")
grep -q 'gradle_build/compress_native_libraries=true' tools/release.sh \
  || fail+=("tools/release.sh no longer forces gradle_build/compress_native_libraries=true")

if [ ${#fail[@]} = 0 ]; then echo "[apksizecheck] RESULT: ok"; exit 0; fi
printf '  %s\n' "${fail[@]}"
echo "[apksizecheck] RESULT: FAILED ${#fail[@]} problem(s): ${fail[0]}"
exit 1
