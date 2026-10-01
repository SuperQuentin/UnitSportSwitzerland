# Screens and the app flow

- **The client boots to a title screen, not a world** (`Core/GameShell`, `src/Ui/`). `Main` adds a
  `GameShell`; the world (`ClientWorld`) is only built when a mode is picked, and is added beside the
  shell as `/root/Main/World`. That is the RPC path: the world must never be a child of the shell or
  be renamed. Flow: Title -> Loading -> InWorld -> (pause -> Leave) -> Title.
- **Pages** are `Ui/Screen` subclasses on the shell's stack (CanvasLayer 40, one shared theme):
  `TitleScreen` (Play solo / Multiplayer / Settings / Controls / Quit over `TitleDiorama`),
  `SoloScreen` (Explore and GPX cards + session options), `GpxPicker`, `MultiplayerScreen`,
  `SettingsScreen` (tabs), `PauseScreen`. `Shell.Push` / `Shell.Back`; Esc / pad B pops,
  and is always consumed (the title refuses, see `core/menu-refuses-close-still-consume`).
  Pages are created fresh on every push, so a page always shows the current settings.
- **Pause menu** (`Ui/PauseScreen`): Esc / Start in the world raises `ClientWorld.PauseRequested`;
  the world does not pause (online nothing can). While any page is up the shell holds `UiFocus`
  (movement keys) and the pointer; closing the last page calls `ClientWorld.ResumeControl` (mouse
  captured again in Explore/Multiplayer). `ClientWorld.MenuOpen` is the shell's answer, so the
  world ignores its keys while a menu or the loading screen is up.
- **Command-line runs skip the title** (`GameShell.UseTitle`): only a whitelist of harmless flags
  (`--name`, `--chunks`, `--rings`, `--menucheck`...) shows it; anything else (`--connect`, `--gpx`,
  `--shot`, every probe) builds the world at once with the shell in `Direct` mode, as before. A new
  probe flag therefore needs no change here. `--menu`/`--settings` in a direct run open the pause
  menu / settings over the world. A direct run keeps the old disconnect behaviour (a chat line).
- **Flags**: `--settings`, `--multiplayer`, `--solo` open that page on the title; `--autostart` goes
  straight into Explore through the loading screen (with `--menu`: the pause menu once in);
  `--uishot <png> [seconds]` saves the screen and quits, for screenshots of the menus.
- **Checks**: `--menucheck` (title Esc, settings tabs with RB, solo, Explore behind the loading
  screen, pause with Esc / Start / B, leave) and `--leavecheck` (see `ui/teardown`).
