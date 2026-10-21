# Held keys that must not count: switches and stuck keys (#1002)

`Core/PlayerInput.Release.cs`. Both act on what `PlayerInput.Held` / `Strength` / `Move` / `HeldButton`
report; code that reads `Input.IsActionPressed` itself is not covered.

- **`PlayerInput.HoldOff()`**: every action held at the call reads as released until it is let go,
  then counts from its next press (swept in `PlayerInput._Process`). `ClientWorld.ToggleMode` calls
  it: the Shift of Shift+T used to sink the spectator camera at once, and a key held into the body
  would sprint it. Call it at any switch whose keys mean something else on the other side.
- **Stuck keys (Windows, windowed)**: Godot knows a key is up only from its key-up event. A long
  hitch can lose it (Windows' "not responding" ghost window takes the input), and the key then stays
  held. `PlayerInput` tracks the physical keys it saw go down and, ten times a second while the
  window has focus, asks `GetAsyncKeyState`; a key up there on two checks running is released through
  `Input.ParseInputEvent` (a real key-up for every action bound to it), logged `[input] W was let go
  during a hitch: released`.
- **Physical keys go through the scan code**: bindings are physical (US positions) but virtual keys
  follow the layout: on AZERTY the physical W is VK 'Z', on QWERTZ the physical Z is VK 'Y'.
  `VirtualKey` maps letters and digits by scan code with `MapVirtualKeyW`; modifiers, Space, arrows,
  Enter, Tab, Esc, Backspace, Home/End and F1-F12 by their fixed VK; punctuation is not checked.
- Off headless, off unfocused, and off while `MouseCapture.Disabled` (a check driving the keys in a
  window). A windowed check that fakes a key press with `ParseInputEvent` sees it released within
  0.2 s: hold it with `Input.ActionPress` instead.
- Checks: `--spectatorcheck --world fixture` (Shift+T as key events: no sink on the held Shift; windowed
  it also fakes a W Windows never saw and expects it let go), `--creativecheck --world flat` (the hold-off
  on `Held`/`Move`, the 26 letters mapping to 26 letter VKs on this layout).
