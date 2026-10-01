# Core: modes, input, settings, diagnostics (`src/Core/`)

Boot, game modes and menu, the input facade, settings, performance tools, teleport and spawn.

Index only: one line per note in `docs/notes/core/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/core`.

## Architecture

- `modes` — Modes: (`GameMode`, `Core/WorldLaunch`): Explore / GpxReplay / Multiplayer, picked on the title screen or by --connect/--gpx; Esc = pause menu
- `input` — Input: (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered in code at boot...
- `settings` — Settings: (`Core/GameSettings`, `Ui/SettingsScreen` tabs, `user://settings.json`): render distance in tile rings (6..40,...
- `performance-overlay` — Performance overlay: (`Core/PerfOverlay`, F3 cycles Off / FPS / Detailed, saved as `GameSettings.PerfOverlay`, also...
- `session-perf-log` — Session perf log: (`Core/PerfRecorder`, F4 start/stop, `--perflog [seconds]` from boot, Settings -> "Open folder"):...
- `teleport` — Teleport: (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at construction. Flying camera...
- `key-hints` — Key hints: (`Core/InputHints`): never type a key into a UI string; bindings named for the device in hand, prompt bar, F1...
- `permissions` — Permissions: (`Core/Permissions`): what the menus may offer; online, spawning a vehicle is an admin's...
- `floating-origin` — Floating origin (#185): world space follows the camera; keep `GlobalPos` or handle `IOriginShiftAware`; containers; Jolt kinematic teleport; `--origincheck`, `--originstress`

## Commands

- `commands` — Commands: --at, --chatcheck, --origincheck, --originshift, --originstress, --goto, --menu, --nohud, --origin, --path, --probe, --shot, --shot-queue (g heights, frame=), --title

## Gotchas

- `never-capture-thing-player-controls` — Never capture "the thing the player controls" at startup
- `footplayer-spectatorcamera-read-physical-keys` — `FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame
- `mode-owns-screen-drop-anchors` — A mode that owns the screen must drop the anchors of the mode it replaced
- `never-default-world-origin-lv95` — Never default the world origin to LV95 0/0: Switzerland is 2.6 million metres from there, so float precision...
- `menu-refuses-close-still-consume` — A menu that refuses to close must still CONSUME the key
- `driving-settings-panel-through-godot` — Driving the Settings panel through the godot-ai MCP changes real settings
- `macos-launch-steals-focus` — Every Godot launch (and editor play) steals focus on macOS; --headless draws nothing; use --shot-queue
