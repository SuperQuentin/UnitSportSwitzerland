# Modes

- **Modes** (`Core/MainMenu`, `GameMode`): Explore / GpxReplay / Multiplayer. `ClientWorld`
  owns the switching; **Esc** opens the picker, and it is shown at boot unless a mode was
  named on the command line (`--connect`, `--gpx`) or a verification tool is running
  (`--shot`, `--probe`). Each mode owns the camera while it runs, so `GpxSession.Begin`/`End`
  activate the playback camera + HUD and hand the previous camera back on the way out —
  `SetReturnCamera` matters because Explore may have swapped to the on-foot camera since.
  The menu also owns the mouse: opening releases the pointer, closing recaptures it, which
  is why `SpectatorCamera` no longer handles Esc. `--menu` forces the picker open (and is
  how it gets screenshotted).
