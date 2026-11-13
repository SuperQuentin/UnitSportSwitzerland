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
- **The key-up copies the key-down** (`PlayerInput.KeyUp`, #1283): its `Device`, `Keycode`, `KeyLabel`
  and `Location`. Godot keeps an action pressed *per device*, and real keys come as
  `InputEvent.DeviceIdKeyboard`, not 0; `Input.IsKeyPressed` goes by the keycode. The first version
  parsed a bare physical key-up on device 0: it logged the release and let go of nothing (Shift kept
  sprinting). A check that fakes keys with `ParseInputEvent` sends them as the keyboard does
  (`SpectatorSwitchCheck.KeyEvent`), or it cannot see this.
- **Late repeats** (#1805): Godot on Windows drops `WM_KEYUP` for Shift and makes Shift's key-up from
  raw input, which `process_events()` drains *before* the message queue. An auto-repeat `WM_KEYDOWN`
  queued just before the release (any frame boundary; surely across a hitch) is then parsed *after*
  the key-up. `Input` keeps an echo out of its pressed keys (`IsPhysicalKeyPressed(Shift)` false) but
  presses the actions on it (`allow_echo`), so sprint / the spectator's Down stayed held with no key-up
  to come, and the stuck-key check, which follows only real presses, never saw it. `TrackKey` now
  releases an echo of a key Godot holds up at once (`KeyUp` of the echo: only that key's part of each
  action, Q still holds Down), logged once per release `[input] Shift repeated after its key-up:
  released`. Only Shift's key-up comes from raw input; `Input.IsKeyPressed(Key.Shift)` (shift-clicks)
  was never affected. An echo also comes for a key held while the window gains focus: it no longer
  presses anything until pressed again.
- **Keys are tracked from `Root.WindowInput`**, emitted before any node's `_Input`, so a UI that handles
  a key away does not hide it from `PlayerInput`.
- **Physical keys go through the scan code**: bindings are physical (US positions) but virtual keys
  follow the layout: on AZERTY the physical W is VK 'Z', on QWERTZ the physical Z is VK 'Y'.
  `VirtualKey` maps letters and digits by scan code with `MapVirtualKeyW`; modifiers, Space, arrows,
  Enter, Tab, Esc, Backspace, Home/End and F1-F12 by their fixed VK; punctuation is not checked.
- Off headless, off unfocused, and off while `MouseCapture.Disabled` (`--nocapture`, a check driving
  the keys in a window; not the Alt free cursor, under which it runs). A windowed check that fakes a key press with `ParseInputEvent` sees it released within
  0.2 s: hold it with `Input.ActionPress` instead.
- Checks: `--spectatorcheck --world fixture` (Shift+T as key events: no sink on the held Shift; `KeyUp`
  of a keyboard key-down lets go of the action, the keycode and the physical key, headless too; windowed
  it also fakes a W Windows never saw and expects it let go), `--creativecheck --world flat` (the hold-off
  on `Held`/`Move`, the 26 letters mapping to 26 letter VKs on this layout). `--spectatorcheck` also sends Shift let go with
  a repeat behind its key-up (left, right, with W, Q, after Shift+T, an Alt tap, a 300 ms hitch).

## The press that opened a view (#1813)

A view opened from E's press (`TryInteract`) opens in that same frame wherever its open is not a
server round trip: offline every server-granted view, and pinball / table games online too. Its
`_Process` then sees E `IsActionJustPressed` as its own: the telescope stepped straight back
("looked through by 1" then "free" in the log), pinball started a game, the table sat you down.
A view reading its keys with `Input.IsActionJustPressed` takes them through `Core.FreshPress`
(`_leave.Take(ActLeave)`: only once the key has been up since it opened). Views reading events
(`_UnhandledInput`) do not see the opening event again; `FloorPicker`'s `fresh` is the same guard.
