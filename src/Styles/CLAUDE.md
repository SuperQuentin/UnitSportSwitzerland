# Visual styles: style kit, material roles, tree LOD (`src/Styles/`)

PS1, Cartoon, Realistic− and Realistic+ looks: every world material comes from `StyleKit` by role,
through a fallback chain down to PS1. Plan and prototype results: `docs/plans/visual-styles.md`.

Index only: one line per note in `docs/notes/styles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/styles`.

## Architecture

- `style-kit` — `VisualStyle` (client-only), `MaterialRole`, `StyleKit.Material(role)`, the fallback chain to PS1, `--style-report`; never load a `ps1_*` shader directly
- `tree-lod` — 3D trees near, ray-traced billboards far (17 plane tests per pixel), complementary dither crossfade, per-tile range from the AABB incl. height, `world_cam_pos`

## Commands

- `commands` — --style, --style-report, --tree-lod, --tree-near, tools/style-shots.sh (and when to discard a run)
