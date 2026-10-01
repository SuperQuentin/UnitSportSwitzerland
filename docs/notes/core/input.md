# Input

- **Input** (`Core/PlayerInput`): every gameplay control is a named `InputMap` action registered
  **in code** at boot (`PlayerInput.Install`, called from `ClientWorld._Ready` after
  `GameSettings.Load` so the saved deadzone applies), bound to keyboard (physical keycodes, so
  AZERTY still works), mouse and gamepad. Query through the static facade (`Move`, `LookRate`,
  `Steer`, `Held`, `Strength`, `Rumble`) rather than `Input.IsPhysicalKeyPressed`: it returns
  neutral while `UiFocus.TextEntryActive`, so callers no longer each check for typing. Pad layout:
  left stick move/steer, right stick look (squared response, `StickSensitivity`/`InvertY`),
  A jump, B slide, L3 sprint (latched until the stick is released), RT/LT throttle/brake (analog
  straight into `RideInput`), X tuck/sprint, Y interact (and the travel picker when there is nothing
  to interact with), R3 camera toggle, Start menu, D-pad down fly/foot toggle (and `tune`, the garage menu, in a stopped car at a garage — T too), D-pad ← / → soft top /
  headlights in a car (O / L; #48), U / P car radio next / previous station (keyboard only; #179), G / X `car_door` (a tap at a car; G / X held is still gathering). Menus call `PlayerInput.FocusFirst` on open so Godot's built-in
  `ui_*` actions drive them with the D-pad, and `MainMenu` holds `UiFocus` while open or the
  stick navigating it would also walk the player. M (place search) stays keyboard-only: a pad
  can't type in it. The facade is the seam an OpenXR backend plugs into later.
  Godot's built-in `ui_accept`/`ui_cancel` have **no** face buttons by default (the D-pad moved
  focus but A pressed nothing), so `RegisterActions` adds A/B and the left stick to the `ui_*`
  actions. **GPX replay** has its own pad layout in `GpxSession.HandlePad` (A play/pause, Y
  camera, X snap, RB next runner, LB hide UI, D-pad ←/→ seek 10 s, ↑/↓ speed; right stick looks
  in Free), read in `_Input` rather than `_UnhandledInput` because a HUD button left focused by a
  mouse click would otherwise swallow A and the D-pad.
  **Trucks and buses (#70)**: `couple` H / D-pad ←, `kneel` K, `destination` N, `shift_up` / `shift_down` Shift / Ctrl and RB / LB (a truck has no tricks, boost or hop), `clutch` C / B (held), `gear_1`..`gear_6` the number keys (only hotbar slots on foot), `gear_r` `  `, `gear_n` 0, `retarder_up` / `retarder_down` ' / ;. Space is the spring brake, G the bus doors.
  **Keyboard layout (issue #32)**: E only interacts (get in/out, search, door) — it used to fall
  back to the travel picker, which then popped up one step too far from a car; the picker is **R**.
  Inventory **I / Tab**, place search **M**, engine **Z**, controls **F1**. `PlayerInput.DeviceChanged`
  fires when the player switches between keyboard and pad. Never type a key into a UI string: see
  the `key-hints` note.
