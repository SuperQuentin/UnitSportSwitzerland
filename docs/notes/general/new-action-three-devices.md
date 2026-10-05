# Every new action is designed for keyboard, gamepad and VR at once

A new key, action or interaction is not done when it works on the keyboard. Decide all three
ways before writing it, and write them down in the same change:

1. **Keyboard**: a named `InputMap` action in `PlayerInput.RegisterActions` (physical keycode),
   never a raw key read (`core/input`, `core/input-conventions`).
2. **Gamepad**: a button in the same action, chosen by context: what that button already means
   in this mode (on foot, mounted, flying, a truck). Reuse a meaning that fits (G / X is "the
   door-like thing here": car doors, bus doors, gangways, a boat trailer's launch) before taking
   a new button. No free button: a radial or a hold, never "keyboard only".
3. **VR**: check it against the rules R1-R6 of `xr/vr-action-map` (the full table of every action
   and its VR way):
   - a thing the player could reach (lever, latch, door, crank, handle) is **gripped by hand** (R1);
   - otherwise the pad button reaches VR through `XR/XrPad` (face buttons, triggers, the right
     stick as the D-pad when mounted, R3);
   - no action may be unreachable (R5): a cab control, a poke button, the wrist menu or a radial.
4. **Prompts and help**: show it through `InputHints` (`{action}` placeholders, never a typed
   key), so each device names its own control (R6); add the row to `Core/ControlsHelp` (F1).
5. **Write it down**: a row in `xr/vr-action-map` with its status (ok / gap / new), and the
   controls on all three devices in the feature's own note.

If the VR way is not built in the same change, the row says **gap** and names the target, and
the PR says so under "not done".

**Why:** actions were added keyboard-first and about twenty ended up with no pad or VR way at
all (#440 found them); fixing them afterwards meant re-plumbing input that had shipped.
