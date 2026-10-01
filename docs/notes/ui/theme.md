# Menu theme and kit

- **One look** (`Ui/UiTheme`): a code-built `Theme`, dark glass panels (`GlassPanel`: bg
  0.055/0.065/0.085 at 82 %, 1 px 7 % white border, radius 12, soft shadow), hairline separators,
  amber accent `(0.98, 0.72, 0.10)`. Focus is an amber outline, so pad focus is always visible.
  Toggle pills, tick boxes, slider grabbers, the dropdown chevron and the line icons (`Ui/Icons`)
  are drawn into `ImageTexture`s at startup: no image assets.
- **Font**: a `SystemFont` (Inter, Segoe UI Variable, Segoe UI, SF Pro, Roboto, Noto Sans, Arial),
  so nothing is shipped and no licence is carried. Bundle a font only with its OFL file and a
  `THIRD_PARTY_LICENSES.md` entry.
- **Applied per root Control, never on the root Window** (shell root, loading screen, modals, the
  chat panel): on the Window it would restyle and resize every HUD at once. A HUD adopts the look by
  theming its own root — how, step by step: `style-guide`.
- **Kit** (`Ui/UiKit`): `MenuButton` (hover/focus slides the label 12 px right and fades in an amber
  bar; the stylebox's left margin is tweened, because a container owns its children's positions),
  `Button(primary)`, `IconButton`, `Card`, `Section` (letter-spaced caps), and the setting rows
  `SliderRow` / `OptionRow` / `ToggleRow` / `ActionRow`. Sliders commit on drag end, keyboard and pad
  steps at once (same rule as before). `Ui/Modal`: `Prompt`, `Confirm`, `Form`, `Inform`; it answers
  Esc in `_Input` before any page, gives focus back on close, and holds `UiFocus` while open.
