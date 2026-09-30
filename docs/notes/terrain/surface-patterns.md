# Surface patterns

- **Surface patterns**: `CoverPalette` writes a `SurfacePattern` code into vertex-colour
  **alpha in quarter steps** (0 none, 0.25 parking bays, 0.5 vine rows, 0.75 mown stripes)
  and `ps1_terrain.gdshader` dispatches on `int(COLOR.a * 4 + 0.5)`. TLM records no
  orientation for any of them, so every pattern runs on world axes — and the direction
  cannot be recovered from the screen-space normal (see the confetti gotcha below).
  Alpha interpolates across a class boundary, so a pattern bleeds one 2 m cell.
