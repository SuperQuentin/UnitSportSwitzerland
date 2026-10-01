# UI style guide: making any screen or HUD look like the menus

Use this when a feature adds or reworks an interface (HUD, picker, panel, popup) and it should match
the menus. The tools are `Ui/UiTheme` (colours, fonts, styleboxes, the `Theme`) and `Ui/UiKit`
(ready-made controls); details of both: `theme`. Worked example: `Core/ChatUi` (floating lines,
glass panel, completion popup).

## Principles

- **Nothing on screen unless it is in use.** A HUD that shows information shows it, then fades
  (chat lines: 9 s, then a 1.2 s fade). A panel appears when opened and is gone when closed.
- **Dark glass, one amber accent.** Panels are translucent dark glass with rounded corners and a soft
  shadow; amber `(0.98, 0.72, 0.10)` marks what is selected, focused or primary — nothing else.
- **Text over the world needs no box.** Draw it with an outline and a shadow instead (below).
- **Compact.** Bottom-left corner HUDs ~480 px wide with a 16 px gutter; popups overlay the panel they
  belong to rather than pushing it taller.
- **Code-built, no assets.** Styleboxes, icons (`Ui/Icons`) and fonts come from code; no PNG skins.

## How to apply it

1. **Theme the root Control of the feature, never the Window.** `new PanelContainer { Theme = UiTheme.Get() }`
   on the panel (or any Control root under a `CanvasLayer`). Setting it on the root Window restyles
   and resizes every other HUD. A `CanvasLayer` is not a Control: put a Control under it.
2. **Panels:** `UiTheme.GlassPanel(alpha, radius, margin)` as the `"panel"` stylebox override.
   Screen panel: defaults (0.82, 12, 22). HUD panel: `(0.78, 10, 10)`. Popup / dropdown over another
   panel: `(0.97, 8, 4)` with `ShadowSize = 12`. Inner cards: `UiKit.Card`.
3. **Text:** `UiKit.Text(text, size, color, bold)`. Sizes `UiTheme.FontBody` 15, `FontSmall` 13,
   `FontTiny` 11, `FontHeading` 22, `FontTitle` 44. Colours `UiTheme.Text` (normal), `TextDim`
   (secondary), `TextFaint` (hints, placeholders, ghost text), `Amber`, `Good` / `Warn` / `Bad`.
   Section headings: `UiKit.Section("Chat")` (small letter-spaced caps). Rich text: set
   `normal_font` = `UiTheme.Font`, `bold_font` = `UiTheme.Bold`, and escape `[` as `[lb]` in
   anything a player typed.
4. **Controls:** `UiKit.Button(text, primary)` (primary = amber fill, one per screen), `IconButton`,
   `MenuButton` (big menu entries), `SliderRow` / `OptionRow` / `ToggleRow` / `ActionRow` (settings),
   `UiKit.Line()` (hairline), `UiKit.VBox/HBox(separation)`. A `LineEdit` under the theme already gets
   the field look with an amber focus border and amber caret.
5. **Lists and selection** (completions, pickers): rows are `PanelContainer`s with
   `UiTheme.Flat(transparent, 6, 10, 4)`; the selected row gets `UiTheme.Flat(new Color(Amber, 0.16f), 6, 10, 4)`
   and amber text. Show a window of ~6 rows that follows the selection instead of a tall list.
6. **Floating text over the world** (chat lines, notifications): no box; `outline_size` 4 with
   `font_outline_color` black at 0.55, `font_shadow_color` black at 0.45, shadow offset (1, 2),
   `shadow_outline_size` 6. Fade in over ~0.2 s, out over ~1 s, by `Modulate.a`.
7. **Modals and prompts:** `Ui/Modal` (`Prompt`, `Confirm`, `Form`, `Inform`) rather than a new dialog.
8. **Keyboard:** a text field registers with `UiFocus` while focused (movement reads raw keys) and
   gives the mouse back with `Input.MouseMode = Visible`, recapturing with `MouseCapture.Capture()`
   on close. Esc closes; pad focus must stay visible (the theme's amber outline does it).

## Checklist for a PR

- Root Control themed with `UiTheme.Get()`, no stylebox colours invented locally (use `UiTheme` colours).
- Nothing permanent on screen that is not needed right now.
- Screenshot of it over a bright scene (snow / sky) and a dark one: still readable.
