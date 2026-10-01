# A menu that refuses to close must still CONSUME the key

- **A menu that refuses to close must still CONSUME the key.** The old boot menu only closed on Esc
  when a mode was running, and otherwise left Esc unhandled, so the same event fell through to
  `ClientWorld._UnhandledInput`, whose Esc handler re-opened the menu: Esc visibly did nothing.
  Now `GameShell._UnhandledInput` consumes Esc / B whenever a page is up, even when the page refuses
  to go (`TitleScreen.OnBack` returns false: there is nothing behind the title), and
  `ClientWorld` ignores every key while `MenuOpen` is true. Check:
  `<godot> --headless --path . -- --menucheck` (title Esc, settings, solo, pause with Esc / Start / B,
  leave; non-zero exit on the first failure).
