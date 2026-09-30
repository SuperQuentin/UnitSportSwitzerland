# Input

- **Input** (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered
  **in code** at boot (`PlayerInput.Install`, called from `ClientWorld._Ready` after
  `GameSettings.Load` so the saved deadzone applies), bound to keyboard (physical keycodes, so
  AZERTY still works), mouse and gamepad. Query through the static facade (`Move`, `LookRate`,
  `Steer`, `Held`, `Strength`, `Rumble`) rather than `Input.IsPhysicalKeyPressed`: it returns
  neutral while `UiFocus.TextEntryActive`, so callers no longer each check for typing. Pad layout:
  left stick move/steer, right stick look (squared response, `StickSensitivity`/`InvertY`),
  A jump, B slide, L3 sprint (latched until the stick is released), RT/LT throttle/brake (analog
  straight into `RideInput`), X tuck/sprint, Y mount picker, R3 camera toggle, Start menu,
  D-pad down fly/foot toggle. Menus call `PlayerInput.FocusFirst` on open so Godot's built-in
  `ui_*` actions drive them with the D-pad, and `MainMenu` holds `UiFocus` while open or the
  stick navigating it would also walk the player. Tab (place search) stays keyboard-only: a pad
  can't type in it. The facade is the seam an OpenXR backend plugs into later.
  Godot's built-in `ui_accept`/`ui_cancel` have **no** face buttons by default (the D-pad moved
  focus but A pressed nothing), so `RegisterActions` adds A/B and the left stick to the `ui_*`
  actions. **GPX replay** has its own pad layout in `GpxSession.HandlePad` (A play/pause, Y
  camera, X snap, RB next runner, LB hide UI, D-pad ←/→ seek 10 s, ↑/↓ speed; right stick looks
  in Free), read in `_Input` rather than `_UnhandledInput` because a HUD button left focused by a
  mouse click would otherwise swallow A and the D-pad.
