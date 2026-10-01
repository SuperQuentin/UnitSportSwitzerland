# Visual styles: style kit, material roles, tree LOD (`src/Styles/`)

PS1, Cartoon, Realistic− and Realistic+ looks: every world material comes from `StyleKit` by role,
through a fallback chain down to PS1. Plan and prototype results: `docs/plans/visual-styles.md`.

Index only: one line per note in `docs/notes/styles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/styles`.

## Architecture

- `style-kit` — `VisualStyle` (client-only), `MaterialRole`, `StyleKit.Material(role)`, the fallback chain to PS1, `--style-report`, per-style looks (MeshDetail, finest stride, sun, environment), live `Restyle` + `RebuildVisuals`, `/style`; never load a `ps1_*` shader directly
- `role-bodies` — shaders/body/ role bodies + thin per-style wrappers, shaders/common/ (world, retro, sightline, style), the `retro` uniform the kit turns off outside PS1, the STYLE_LIT path
- `tree-lod` — 3D trees near, ray-traced billboards far (17 plane tests per pixel), complementary dither crossfade, per-tile range from the AABB incl. height, `world_cam_pos`, NearTrees (per-tree culling of heavy trees), Cartoon's traced shapes
- `cartoon` — Cartoon: lit wrappers, cel light (shadow faded at the terminator against acne, hard shadows), grade, sky/haze/sun, who casts shadows, toon figures, settings entry

## Commands

- `commands` — --style, /style, --style-report, --tree-lod, --tree-near, tools/style-shots.sh (Windows, pixel checks, when to discard a run), chat lines in --shot-queue
