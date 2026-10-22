# Standalone Quest: the game as a Meta Quest app (#1112)

The same VR mode as over Link (`setup`, `rig`, `controls`), run on the headset's own Android, with no PC.

## Building and installing

- `tools/quest.sh [--install] [--debug] [--out <apk>]` (Git Bash). It builds HEAD plus the uncommitted changes to
  tracked files in a temporary worktree under `%TEMP%/unitsport-quest` (`QUEST_STAGE`), never in the project
  (`general/` builds inside the project broke the game before). It prints a warning for untracked files, which are not
  in the build. `--install` runs `adb install -r` on the plugged-in headset and starts it.
- **The OpenXR vendors addon** (`godot_openxr_vendors`, pinned in the script; 5.x needs Godot 4.6+) is fetched once
  into the stage and copied into the build worktree only. Its 80 MB of binaries are never committed, and desktop runs
  never load its GDExtension. The `--import` registers it (`.godot/extension_list.cfg`); its editor side is the
  export plugin that adds Meta's loader and manifest (the VR intent category, the supported headsets, hand tracking, the
  overlay keyboard).
- **The "Quest" preset** is appended to a copy of the per-machine `export_presets.cfg`, so the repo's file is never
  touched. It is a Gradle build, `xr_features/xr_mode=1` (OpenXR) with `enable_meta_plugin`, arm64 only, and
  `custom_features="quest"`. Its package is `ch.unitsport.switzerland.quest`, so it installs beside the phone app.
  `version/code` is the commit count.
- **Signing:** as the phone test APKs: a release export signed with `GODOT_ANDROID_KEYSTORE_RELEASE_*`, or
  `--debug` with the editor's debug keystore. A build signed with another key needs `adb uninstall
  ch.unitsport.switzerland.quest` first.
- **The headset:** developer mode on (the Meta Horizon phone app → the headset → Developer mode), USB to the PC,
  "Allow USB debugging" accepted in the headset. Logs: `adb logcat -s godot:*`.

## What differs in the game

- **`Platform.IsStandaloneVr`** is the export's `quest` feature. `IsMobile` stays true there: client only, no SDL,
  no self-update, tiles streamed into `user://`, the phone's first-launch defaults (`core/platform`).
  **`Platform.IsTouch`** (= mobile and not standalone VR) gates what only a touch screen wants: the touch overlay, the
  finger-sized menus (`MobileUiScale`), the touch rows in Settings, the confined pointer, the chat hint and the
  engine label's phone place.
- **OpenXR from the engine's start.** The export's XR mode makes Godot start with `--xr_mode_openxr`, so
  `XrSession.TryStart` finds it running. Nothing relaunches (Android cannot), and the VR toggles (title, Settings)
  are hidden: `CanSpawnProcesses` is false. Settings keeps the headset's own rows (resolution, MSAA, turning, comfort).
- **72 Hz** (`XrSession`): the Quest 2's own GPU budget. Over Link it stays 90.
- **No monitor.** `XrMonitor` forces `Off`, so the window renders no world, only the 2D the UI panel shares
  (`XrUi`). Nobody sees the headset's Android window.
