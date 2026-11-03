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
  Android side. Godot bundles the OpenXR loader (`org.khronos.openxr:openxr_loader_for_android`) and the engine starts
  OpenXR only when `xr/openxr/enabled` is on for that export, read through its feature tags: `project.godot` has
  `openxr/enabled.quest=true`, so only the Quest export turns it on (desktop runs never load OpenXR). Without it the
  headset showed the loading dots forever: first `OpenXR did not start on this headset`, then, with `--xr-mode on`,
  `libopenxr_loader.so not found`. With it `XrSession.TryStart` finds OpenXR running. Nothing relaunches (Android cannot), and the VR toggles (title, Settings)
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
- **A scripted run:** `tools/quest-measure.sh <label> 150 <game args>` puts the arguments in `user://cmdline.txt`
  (`Core/CmdArgs` reads it on Android), keeps the headset worn and awake (`prox_close`, Guardian paused), starts the
  game and prints VrApi's averages over the last 40 s (startup takes ~90 s). Real tiles for a deterministic run: copy
  a few tiles plus a manifest listing only them into `files/terrain_chunks` (through `/data/local/tmp` and `run-as`),
  the folder Android reads anyway (no `--chunks`). `--seat kart` gives a street view that stays the same (a phone
  or headset tries the seat for 90 s); two runs of the same scene still differ by about 2 ms of GPU time.
- **Splitting the frame:** `--hidenodes Ground,Roads,Trees,Broadleaves,Water,Horizon,Buildings` hides those tile
  layers (`Core/RenderCensus`), `--rendercensus` prints triangles per branch. `adb shell ovrgpuprofiler -r` reads the
  GPU's counters; `adb shell simpleperf record --app <package> -g` samples the CPU (a debug export only; C# shows as
  `unknown`, so rank it with `dotnet-trace` on a `--xrsim --quest` desktop run of the same place).
- **A headset screenshot:** `adb shell am startservice -n com.oculus.metacam/.capture.CaptureService -a
  TAKE_SCREENSHOT`, then pull the newest file in `/sdcard/Oculus/Screenshots`. `debuggerd -b` needs root; to see where a
  frozen app spins, `simpleperf record --app <package> -g --duration 12` while it is frozen.
- **Online is broken (#1218):** joining a server freezes the main thread ~70 s (a loop in `libmonosgen`), so the server
  drops the headset. Measure offline until it is fixed. A server wakes the headset at its sleeper
  (`user://sleepers/server.json`), which may be off a fixture world or on tiles the headset lacks.

## What a Quest 2's GPU cannot afford

Since #1216 these are the **Very low** Detail preset on every device (`terrain/detail-presets`), not a property of the
headset: a Quest gets them because its `VrDetail` defaults to Very low.

- **`discard`** in any shader turns off the Adreno's early depth rejection (LRZ) for everything drawn after it, so
  every hidden layer is shaded: the `_standalone` shader variants (`StyleKit` takes `<shader>_standalone` where it
  exists, on Very low: `GameSettings.Lean`) define `NO_DISCARD`, `NO_SCENE_DEPTH` (water: no depth read, opaque) and `STANDALONE` (flat windows, no
  ground patterns).
- **Whole-tile meshes:** a tile's buildings and roads are cut into 125 m cells (`BuildingOcclusion.Cells`), so the
  frustum and occlusion culling drop what is behind the eye or a block. Past `LodPolicy.BuildingBoxFromM` (Very
  low's 300 m, by 3D distance from the eye to the cell, at any height) a cell's buildings are their roof on walls
  (`BuildingMeshBuilder.BuildProxies`: the surveyed roof triangles, walls under the roof's outer edges; a plan box
  reached across the street of an L or a ring). No fade there: Godot's dithers with a discard. Instead the near
  buildings' windows fade into the wall by 0.7 of that distance (`building.gdshaderinc`, `world_building_box_from`),
  so the windowless stand-in takes over unseen. Road paint stops at `LodPolicy.RoadPaintToM`.
- **Application SpaceWarp** works (`--spacewarp on` on a `QUEST_SPACEWARP=1 tools/quest.sh` build: `XrSession.ApplySpaceWarp`; registered and switched off, the extension crashed the game as the session began. Registered by
  `tools/quest.sh`) but is off: a black patch flashed in the left eye, and the made frames felt less smooth than the
  headset's own reprojection of a slower game. Motion vectors also cost ~5 ms a rendered frame.
