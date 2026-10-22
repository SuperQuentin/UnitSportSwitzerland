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
- **OpenXR from the engine's start.** The export's XR mode (`--xr_mode_openxr`) only makes the app a VR one on the
  Android side: the engine itself starts OpenXR only with `xr/openxr/enabled` (off: desktop runs never load it) or
  `--xr-mode on`, which the preset's `command_line/extra_args` passes. Without it the headset showed the loading dots
  forever and the log said `OpenXR did not start on this headset`. With it `XrSession.TryStart` finds OpenXR running. Nothing relaunches (Android cannot), and the VR toggles (title, Settings)
  are hidden: `CanSpawnProcesses` is false. Settings keeps the headset's own rows (resolution, MSAA, turning, comfort).
- **Refresh rate:** the VR one of Link (`air-link`, `VrRefreshHz`, Settings → Video → VR refresh rate), applied
  live with physics ticking at it. 72 by default, the Quest 2's own GPU budget; a standalone headset also offers 60
  (`GameSettings.VrRefreshRates`), physics' original rate, for a scene it cannot hold at 72.
- **Settings:** the window's 3D resolution, anti-aliasing and VSync rows are hidden too: the window draws no world.
- **Without a headset:** `--quest` makes a desktop run a standalone headset (`IsStandaloneVr`, `IsMobile`), so
  `<godot> --path . -- --xrsim --quest [--settings] --xrheadshot <png> 6` shows the panel as the Quest would. With
  `--xrsim` the monitor stays first person, the window being the only view.
- **No monitor.** `XrMonitor` forces `Off`, so the window renders no world, only the 2D the UI panel shares
  (`XrUi`). Nobody sees the headset's Android window.

## Measuring on the headset

- **Frame time:** the perf overlay and the recorder (F4, `--perflog`, `core/` PerfRecorder) time the headset's own
  viewport in VR (`XrRig.HeadsetView`), not the window's, so their GPU column is the eyes' render.
- **Getting the logs off:** `user://` is the app's internal storage. Only a `--debug` export is debuggable, so
  `adb shell run-as ch.unitsport.switzerland.quest ls files/perf_logs` (then `cat` each file) works on it, never on a
  release one. `adb logcat -s godot:*` works on both.
- **Meta's own view:** OVR Metrics Tool (from the Meta Horizon Store) overlays FPS, GPU and CPU levels and GPU %.
