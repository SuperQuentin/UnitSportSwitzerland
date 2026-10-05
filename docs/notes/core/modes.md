# Modes

- **Modes** (`GameMode` in `Core/WorldLaunch`): Explore / GpxReplay / Multiplayer. The title screen
  picks one (`ui/screens`) and builds a `ClientWorld` for it with a `WorldLaunch` (mode, endpoint,
  GPX paths, name, hosted). The command line does the same with `WorldLaunch.FromArgs()`
  (`--connect [host]`, `--gpx <path>` repeatable, else Explore). `ClientWorld.StartLaunch` starts it,
  and `EnterMode` switches inside the world (replay goes back to Explore when it ends). Each mode owns
  the camera while it runs, so `GpxSession.Begin`/`End` activate the playback camera + HUD and hand
  the previous camera back on the way out; `SetReturnCamera` matters because Explore may have swapped
  to the on-foot camera since. Explore from the menus starts on foot (`core/ground-start`), from the
  command line in the fly camera.
- **Esc** in the world opens the pause menu (`ui/screens`), which owns the mouse: opening releases
  the pointer, closing recaptures it (`ClientWorld.ResumeControl`), which is why `SpectatorCamera`
  does not handle Esc. Another mode means leaving to the title (`ui/teardown`).
