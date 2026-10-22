#!/usr/bin/env bash
# Meta Quest APK (#1112): the game as a standalone VR app, built from this checkout (HEAD plus the uncommitted
# changes to tracked files) in a temporary worktree outside the project, so the 80 MB OpenXR vendors addon and the
# Gradle build never land in it. Notes: docs/notes/xr/quest-standalone.md.
# Usage: tools/quest.sh [--install] [--debug] [--out <apk>]   (Git Bash; GODOT=<console exe> to override)
# QUEST_ENGINE_ARGS: engine arguments baked into the APK (e.g. --gpu-profile, a GPU time per render pass in the log).
# --install: adb install -r on the connected headset, then start it. --debug: a debug export (signed with the editor's
# debug keystore). A release export is signed with GODOT_ANDROID_KEYSTORE_RELEASE_PATH/_USER/_PASSWORD.
# Needs what the phone APK needs (docs/notes/core/android-emulator.md): the 4.7.2 mono export templates, the Android
# SDK and a JDK 17 in the editor settings. Fetches the pinned godot_openxr_vendors release once into the stage.
set -euo pipefail
cd "$(dirname "$0")/.."
INSTALL=0; KIND=release; OUT=""
while [ $# -gt 0 ]; do case $1 in
  --install) INSTALL=1 ;;
  --debug) KIND=debug ;;
  --out) OUT=$2; shift ;;
  *) echo "Unknown option: $1"; exit 2 ;;
esac; shift; done
GODOT=${GODOT:-'/c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe'}
VENDORS=5.1.0-stable   # Godot 4.6+ (its plugin.gdextension compatibility_minimum)
PACKAGE=ch.unitsport.switzerland.quest
STAGE=${QUEST_STAGE:-${TEMP:-/tmp}/unitsport-quest}
REPO=$PWD
MAIN=$(cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && pwd)   # the main checkout
[ -n "$OUT" ] || OUT="$STAGE/UnitSportSwitzerland-quest-$KIND.apk"
[ $KIND = debug ] || [ -n "${GODOT_ANDROID_KEYSTORE_RELEASE_PATH:-}" ] \
  || { echo "No GODOT_ANDROID_KEYSTORE_RELEASE_PATH: set the keystore, or pass --debug"; exit 1; }
# export_presets.cfg is per machine (gitignored): this checkout's, else the main checkout's
PRESETS="$REPO/export_presets.cfg"; [ -f "$PRESETS" ] || PRESETS="$MAIN/export_presets.cfg"
[ -f "$PRESETS" ] || { echo "No export_presets.cfg here or in $MAIN"; exit 1; }
export MSBUILDDISABLENODEREUSE=1   # as tools/release.sh: a reused MSBuild node holds Godot's console pipe

mkdir -p "$STAGE"
ADDON="$STAGE/vendors-$VENDORS/asset/addons/godotopenxrvendors"
if [ ! -f "$ADDON/plugin.gdextension" ]; then
  echo "Fetching godot_openxr_vendors $VENDORS"
  curl -fsSL -o "$STAGE/vendors-$VENDORS.zip" \
    "https://github.com/GodotVR/godot_openxr_vendors/releases/download/$VENDORS/godotopenxrvendorsaddon.zip"
  rm -rf "$STAGE/vendors-$VENDORS"; unzip -qo "$STAGE/vendors-$VENDORS.zip" -d "$STAGE/vendors-$VENDORS"
fi

WT="$STAGE/wt"
git worktree remove --force "$WT" 2>/dev/null || rm -rf "$WT"
git worktree add -q --detach "$WT" HEAD
trap 'cd "$REPO"; git worktree remove --force "$WT"' EXIT
git diff HEAD --binary | (cd "$WT" && git apply --allow-empty)
[ -z "$(git ls-files --others --exclude-standard)" ] || echo "WARNING: untracked files are not in the build: $(git ls-files --others --exclude-standard | head -5 | tr '\n' ' ')"
cp -r "$ADDON" "$WT/addons/"
cd "$WT"

# The Quest preset: a Gradle build (the vendors addon is Android AAR plugins), XR mode OpenXR with Meta's loader,
# its own package beside the phone's, and the "quest" feature (Core/Platform.IsStandaloneVr)
# XR mode OpenXR only makes the app a VR one on the Android side; project.godot's openxr/enabled.quest override
# (the base setting stays off for desktop runs) bundles the OpenXR loader and starts OpenXR in the engine
cp "$PRESETS" export_presets.cfg
n=$(grep -c '^\[preset\.[0-9]*\]$' export_presets.cfg)
excl=$(sed -n 's/^exclude_filter="\(.*\)"$/\1/p' export_presets.cfg | head -1)
code=$(git rev-list --count HEAD)
cat >> export_presets.cfg <<EOF

[preset.$n]

name="Quest"
platform="Android"
runnable=false
dedicated_server=false
custom_features="quest"
export_filter="all_resources"
include_filter=""
exclude_filter="$excl"
export_path=""
patches=PackedStringArray()
encryption_include_filters=""
encryption_exclude_filters=""
seed=0
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.$n.options]

custom_template/debug=""
custom_template/release=""
dotnet/include_scripts_content=false
dotnet/include_debug_symbols=false
dotnet/embed_build_outputs=false
gradle_build/use_gradle_build=true
gradle_build/export_format=0
gradle_build/compress_native_libraries=true
architectures/armeabi-v7a=false
architectures/arm64-v8a=true
architectures/x86=false
architectures/x86_64=false
keystore/release=""
keystore/release_user=""
keystore/release_password=""
version/code=$code
version/name="quest-$(git rev-parse --short HEAD)"
package/unique_name="$PACKAGE"
package/name="UnitSport Switzerland"
package/signed=true
screen/immersive_mode=true
command_line/extra_args="${QUEST_ENGINE_ARGS:-}"
xr_features/xr_mode=1
xr_features/enable_meta_plugin=true
meta_xr_features/hand_tracking=1
meta_xr_features/hand_tracking_frequency=1
meta_xr_features/passthrough=0
meta_xr_features/eye_tracking=0
meta_xr_features/face_tracking=0
meta_xr_features/body_tracking=0
meta_xr_features/render_model=0
meta_xr_features/use_overlay_keyboard=true
meta_xr_features/quest_1_support=false
meta_xr_features/quest_2_support=true
meta_xr_features/quest_3_support=true
meta_xr_features/quest_pro_support=true
permissions/access_network_state=true
permissions/access_wifi_state=true
permissions/change_wifi_multicast_state=true
permissions/internet=true
EOF

dotnet build UnitSportSwitzerland.csproj -c Release
# the import registers the addon's GDExtension, whose editor side is the export plugin that adds Meta's manifest
"$GODOT" --headless --path . --import || true
grep -q godotopenxrvendors .godot/extension_list.cfg || { echo "The vendors addon did not register"; exit 1; }
mkdir -p "$(dirname "$OUT")"; rm -f "$OUT"
GRADLE_OPTS="${GRADLE_OPTS:-} -Dorg.gradle.daemon=false" \
  "$GODOT" --headless --path . --install-android-build-template "--export-$KIND" "Quest" "$OUT" || true
[ -s "$OUT" ] || { echo "The export produced no APK"; exit 1; }
# a failed dotnet publish still writes an APK, just without the game
# (counted, not grep -q: an early exit breaks unzip's pipe, which pipefail reports as a failure)
[ "$(unzip -l "$OUT" | grep -c '/UnitSportSwitzerland\.dll$')" -gt 0 ] || { echo "The APK has no UnitSportSwitzerland.dll"; exit 1; }
echo "Quest APK: $OUT ($(du -h "$OUT" | cut -f1))"

if [ $INSTALL = 1 ]; then
  ADB=${ADB:-$(command -v adb || echo "${LOCALAPPDATA:-}/Android/Sdk/platform-tools/adb.exe")}
  "$ADB" install -r "$OUT"
  "$ADB" shell am start -n "$PACKAGE/com.godot.game.GodotAppLauncher"   # GodotApp itself is not exported
fi
