# HUD panels, titles, prompts and amber come from `UiTheme` (#221)

## Rule

- Never hand-build a `StyleBoxFlat` for a panel: `UiTheme.GlassPanel(alpha, radius, margin)` for the
  menu look (`style-guide`), `UiTheme.Flat(bg, radius, marginH, marginV, border, borderWidth)` for a
  flat HUD box. Set a single differing margin on the returned box (`s.ContentMarginBottom = 12`).
- A panel's title is `UiTheme.Title(text, fontSize = 22)` (amber; `0` keeps the inherited size).
- The floating "press E to …" line is `UiTheme.Prompt(aboveBottom)` (18 px, black outline 6, centred
  300 x 30 at `(-150, aboveBottom)` from the bottom centre, hidden).
- Never type the amber `new Color(0.98f, 0.72f, 0.10f)`: `UiTheme.Amber`, `new Color(UiTheme.Amber, a)`
  for a translucent one, `UiTheme.AmberDim` (0.35).

## Why

#221 PR 2: 9 HUD files lost their copies of the panel stylebox, the outlined prompt label and the
amber literal (one implementation each in `Ui/UiTheme`).

## Same logic, preserved

- Pixel-identical: every call passes the values that were there (each panel keeps its own background,
  radius and margins; `Flat` sets the same anti-aliasing, which is the `StyleBoxFlat` default).
  Unifying the backgrounds/margins onto `GlassPanel` would be a visual change: its own PR, with shots.
- `Title` sets only the font size and the colour; alignment and size flags stay with the caller.

## Migrating old code / open branches

- grep `new Color(0.98f, 0.72f, 0.10f` → `UiTheme.Amber` (keep any alpha: `new Color(UiTheme.Amber, a)`).
- grep `new StyleBoxFlat` in a HUD → `UiTheme.Flat(...)` with the same numbers.
- grep `AddThemeConstantOverride("outline_size", 6)` next to `Position = new Vector2(-150, …)` → `UiTheme.Prompt(…)`.
- Not migrated yet because open PRs edit them (do it when you next touch the file, same values):
  `Interiors/InteriorManager` prompt (#269), `Core/ControlsHelp` panel (#169). `RadioUi`, `RideUi` and
  `InventoryUi` already used `UiTheme`. A branch that conflicts in a migrated file: take main's version,
  then re-apply your own change on top of the `UiTheme` call.

## How to check

`dotnet build`; screenshots of the panel before and after (`--photocheck` album, `--garagecheck a`
offline, `--gathercheck,<png>`, `--gpx <track> --shot …`) must match.
