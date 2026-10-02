# Core: modes, input, settings, diagnostics (`src/Core/`)

Boot, game modes and menu, the input facade, settings, performance tools, teleport and spawn.

Index only: one line per note in `docs/notes/core/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/core`.

## Architecture

- `modes` — Modes: (`GameMode`, `Core/WorldLaunch`): Explore / GpxReplay / Multiplayer, picked on the title screen or by --connect/--gpx; Esc = pause menu
- `steering-wheel` — Steering wheel: (`Core/SteeringWheel`, #68): SDL3 wheel and pedals, 1:1 direct steering, force feedback (aligning, soft lock, road, engine, knocks), Godot's copy of the device ignored, presets + Settings → Wheel tab
- `input` — Input: (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered in code at boot...
- `settings` — Settings: (`Core/GameSettings`, `Ui/SettingsScreen` tabs, `user://settings.json`): render distance in tile rings (6..40,...
- `performance-overlay` — Performance overlay: (`Core/PerfOverlay`, F3 cycles Off / FPS / Detailed, saved as `GameSettings.PerfOverlay`, also...
- `session-perf-log` — Session perf log: (`Core/PerfRecorder`, F4 start/stop, `--perflog [seconds]` from boot, Settings -> "Open folder"):...
- `teleport` — Teleport: (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at construction. Flying camera...
- `key-hints` — Key hints: (`Core/InputHints`): never type a key into a UI string; bindings named for the device in hand, prompt bar, F1...
- `permissions` — Permissions: (`Core/Permissions`): what the menus may offer; online, spawning a vehicle is an admin's...
- `licenses` — Licenses page: (`Core/Licenses`, Settings > About tab, `--licenses`): every data source and bundled component with its attribution and link, plus Godot's notices...
- `chat-probe` — MP probes derive from `Core/ChatProbe`; quick self-checks go in `ClientWorld.QuickChecks`, camera-placing tools in the `tools` table (`placedByTool` derived), never a hand-kept list
- `cmd-args` — Read the command line only via `CmdArgs.Has/Value/Float/Double/Int/FlagWithShot` (cached, InvariantCulture); never `OS.GetCmdlineUserArgs()` + `IndexOf` again (#221)
- `is-online` — "online?" is `NetLink.Online(this)`; never copy the `not OfflineMultiplayerPeer && Connected` check again
- `floating-origin` — Floating origin (#185): world space follows the camera, online too (each peer its own origin, LV95 on the wire); keep `GlobalPos` or handle `IOriginShiftAware`; containers; `Follow` for shared point lists; Jolt kinematic teleport; `--origincheck`, `--originstress`
- `json-store` — Persist JSON only via `JsonStore.Save` (atomic, `user://` ok, static options); never `FileAccess` Write / `File.WriteAllText`; InteriorManager still to migrate
- `perf-saves-background` — Gameplay saves (plant, deposit, loot, claim) via `JsonStore.SaveAsync` (one ordered background writer, `SaveQueue`, flushed on quit); never `JsonStore.Save` in an RPC handler
- `mathx` — `MathX.Flat/FlatLength/FlatDistance/Damp/WrapAngle` and `Mathf.SmoothStep`, never a private copy; only where floats stay identical (`-dt / tau` is not `Damp`); tier-0 tested (#221)

## Commands

- `commands` — Commands: --at, --chatcheck, --fakewheel, --ffbcheck, --ffblog, --origincheck, --originshift, --originstress, --goto, --licenses, --menu, --nohud, --origin, --path, --probe, --settings wheel, --shot, --shot-queue (g and i heights, frame=), --title, --wheelcheck, --wheellock, --wheelwatch

## Gotchas

- `never-capture-thing-player-controls` — Never capture "the thing the player controls" at startup
- `footplayer-spectatorcamera-read-physical-keys` — `FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame
- `mode-owns-screen-drop-anchors` — A mode that owns the screen must drop the anchors of the mode it replaced
- `never-default-world-origin-lv95` — Never default the world origin to LV95 0/0: Switzerland is 2.6 million metres from there, so float precision...
- `menu-refuses-close-still-consume` — A menu that refuses to close must still CONSUME the key
- `driving-settings-panel-through-godot` — Driving the Settings panel through the godot-ai MCP changes real settings
- `macos-launch-steals-focus` — Every Godot launch (and editor play) steals focus on macOS; --headless draws nothing; use --shot-queue
- `windows-launch-focus` — Windows: game windows may open but never in front or on top; no --always-on-top / no_focus, no editor play while the user works
- `perf-no-per-frame-allocations` (general) — `PlayerInput` reads use static `StringName`s (`ActionName`); `InputHints.Label`/`Format` are memoised, anything changing the `InputMap` calls `InputHints.Invalidate()`
