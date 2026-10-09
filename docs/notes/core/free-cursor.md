# Free cursor: tap Alt in game (#654)

- `Core/CursorToggle`, a child of `ClientWorld`. Tap Alt: a captured pointer becomes visible while the game keeps
  running (WASD still moves, mouse motion no longer looks: `PlayerInput.IsLookMotion` needs Captured). Tap Alt
  again, or click anything no control takes (`_UnhandledInput`), and `MouseCapture.Capture()` takes it back.
  The recapturing click is consumed; nothing polls Fire or UseItem from the mouse, and `ItemController` ignores
  clicks on an uncaptured pointer anyway.
- A tap, not a hold: it fires on Alt's release, only if no other key went down meanwhile (read in `_Input`, before
  any control). Alt+Enter (fullscreen), Alt+Tab and Alt+F4 are chords and never toggle it.
- It only ever frees a Captured pointer and only acts while no menu is open and no text field has the keyboard.
  A menu, the inventory or the chat that frees the pointer owns it; `Free` drops by itself when the pointer is
  captured again by someone else or a menu opens.
- `free_cursor` is bound to Alt for `InputHints` and the F1 help only. Pad and VR: none on purpose (no cursor;
  VR's laser is its pointer), noted in `xr/vr-action-map`.
- Not covered by an automated check: a headless run has no pointer to capture.
