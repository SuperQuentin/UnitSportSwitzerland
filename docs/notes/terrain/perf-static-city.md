# A city's static things: what they cost and what was cut (#553)

Measured on two routes, `--fly 0,440,0,-65,35,200 --origin 2497500,1116500` (a flight at 440 m
across Geneva) and `--flystreet 10,240 --at 2500300,1118450 --to 2499600,1117100` (2.4 km of real
streets at eye height, `Core/StreetFlight`), render distance 15, a 5 km horizon, High detail,
Cartoon, traffic off, an RTX 4070. `--perflog` frames: frame, GPU, render-thread CPU, draws.

## Where a frame goes

- **These frames are CPU-bound.** Frame 5.0–6.3 ms against a GPU at 4.3–4.9 ms. A change that
  only saves GPU time does not shorten the frame on this machine (it does on a phone, #556, or in
  VR); a change that saves draws does, through the render thread.
- **Buildings**: switching them off entirely (`--debugview nobuildings`) saved 1.1 ms of GPU from
  the air and 1.6 ms at street level (23 % and 37 % of the GPU frame), and next to nothing of the
  frame. Only about 40 draws: one mesh per tile.
- **The dormant layer** (car parks, yards, harbours): 0.27 ms of frame from the air, 0.08 ms at
  street level, GPU unchanged. Not worth a model LOD.
- **Shop and bank signs** were the draws: three each (a plate and two transparent `Label3D`s) on
  every commercial building out to the building ring, with no range. 1 300 of the 1 560 draws at
  street level.

## What was cut

- **Signs drawn to 200 m** (`BankSigns.DrawnM`; the lettering casts no shadow). Draws 2 090 → 684
  from the air and 1 560 → 316 at street level; render-thread CPU halved (3.35 → 1.62 ms,
  2.51 → 1.14 ms); frame p50 5.9 → 5.3 ms and 5.0 → 4.4 ms, p99 13.3 → 8.3 ms and 6.5 → 5.6 ms.
  **Any per-building node needs a visibility range**: a city has thousands of them.
- **A tile past the building or road ring sheds them** (`ChunkManager.RecomputeDesired`,
  `ChunkNode.ClearBuildings/ClearRoads`, `TileUnfurnished` for what hung on them: signs, the IKEA
  pylon, occasion decorations). They used to stay until the tile unloaded, 16 rings out at render
  distance 15: every building a flight passed stayed drawn, and lowering the detail or the render
  distance kept them all. Trees and water, committed with the buildings, stay.
- **Occlusion culling** (`occlusion-culling`), on by default: street p50 4.4 → 4.1 ms, p99 5.6 →
  5.0 ms; from the air mean −4 %; GPU −8 % and −5 %.
