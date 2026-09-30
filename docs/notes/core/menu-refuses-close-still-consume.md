# A menu that refuses to close must still CONSUME the key

- **A menu that refuses to close must still CONSUME the key.** At boot the mode menu opens with no
  mode running; `MainMenu._UnhandledInput` only closed when one was, and otherwise left Esc
  unhandled — so the same event fell through to `ClientWorld._UnhandledInput`, whose Esc handler
  re-opens the menu. Esc visibly did nothing and the menu could only be left with the mouse. With
  nothing to resume, Esc / B now start Explore. Check: `<godot> --headless --path . -- --menucheck`
  (boot menu, Esc, pad Start/B, Esc out of Settings; non-zero exit on the first failure).
