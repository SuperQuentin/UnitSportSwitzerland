# Generated relief: the generator shaped on the real country

- **What** (issue #138, `Terrain/ProceduralWorld*.cs`): the generated fill (`generated-fill`) is
  shaped on `src/Terrain/swiss_relief.gz`, a 500 m heightmap of Switzerland and its border areas
  (swisstopo swissALTIRegio averaged by `tools/swiss_relief.py`, 1101x861 nodes, LV95
  2385000-2935000 / 974000-1404000, 1.7 MB), **embedded in the assembly** so the game, the
  server and the no-Godot tools (BlendCheck) read it alike. It replaced one procedural valley
  running east-west through Riddes for ever, under a noise massif up to ~4000 m right beside it.
- **Relief** (`ProceduralWorld.Relief`, once per process, ~0.5 s): macro height = Catmull-Rom of
  the nodes; roughness = max-min over 3x3 nodes (scales the generated detail: ridged noise up to
  ±110 m in the Alps, a few metres on the Plateau); peak = max over 7x7 nodes (sizes valley
  walls); **lakes** = connected nodes flat to 1 dm with all 8 neighbours (the source models a lake
  as its surface: Geneva 372.1, Neuchatel 429.3), 45 of them, grown by shore nodes; **drainage** =
  a priority flood from the map edge on the integer decimetres (ties by insertion order, so every
  peer derives the same network), areas in one reverse pass of the pop order.
- **Network** (`ProceduralWorld.Network`, once, ~1 s, warmed on a worker by the constructor):
  rivers = nodes draining >= 30 km² not in a lake, walked up from each outlet taking the bigger
  branch as the main stem (so parents come first); path averaged ±2 nodes (the grid's 45° steps),
  Chaikin x2, then a meander of up to 30% of the floor, tapered to 0 at both ends. Bed = macro
  along the path, forced to fall to the mouth, **capped at the parent's bed where it crosses the
  parent's flat floor**. Roads along rivers >= 45 km² (offset from the unmeandered axis, clear of
  the widest meander, pulled in on the inside of bends), railways along rivers >= 500 km² on the
  other side; each joins its parent's line by a straight link. Village slots every 2.6 km of road
  (72% taken, below 1700 m, floor >= 70 m). 2077 rivers, ~1400 lines, ~7000 villages over the map.
  1 km bucket indices for valleys (segments by reach), channels (bank + 2 lattice cells), lines
  (+400 m) and villages.
- **Height** = two world-anchored lattices: **coarse, 25 m** — P, Q with h = P + Q f (macro +
  detail, valleys pressed in: per river V = max over segments of 1 - smoothstep(floor, reach, d),
  F = bed weighted by 1/d^8 (continuous where nearest-point jumps inside a bend), applied smaller
  rivers first), lake weight/level, the strongest valley's floor (cover). **Fine, 5 m** —
  roughness f, nearest channel (level, half-width, bank, depth, wet) and a culvert factor (no
  channel within 8 m of a valley road or railway). The height is computed **at the 5 m points**
  and bilinear between them: a full grid is one interpolation per vertex (19 ms a tile unblended,
  against ~24 ms for the old single valley). `Coarse` lerp returns the corner exactly at t = 0,
  so horizon samples read one lattice point.
- **Gotchas found building it**:
  - A channel may only **lower** the ground. A tributary traced on 500 m nodes comes down a
    flank beside its parent's floor; its bed was 150 m above the flattened floor and "carving"
    raised a 150 m wall (a 48 m-per-metre step). Water is drawn only where the ground is the
    channel's bottom (`Alt < Bed + 0.5`).
  - The channel bucket must reach **two lattice cells past the bank**: a corner just outside it
    saw no channel, and the carve stopped dead inside the cell.
  - A fixed wall span squeezed a kilometre of rise into a kilometre of smoothstep (63° walls);
    the span is rise / 0.55, the rise from the peak field.
  - MSBuild read `swiss_relief.bin.gz` as culture "bin" and moved it to a satellite assembly:
    no `.xx.` infix, and `WithCulture="false"` on the `EmbeddedResource`.
- **Check**: BlendCheck (all seams, resolutions, horizon = grid, point path = grid) and its
  "largest step per metre" line — generated 1.43, the old one's was below that only because it
  had no rivers crossing floors. Map renders of the network are easiest from a scratch console
  project compiling `src/Terrain/ProceduralWorld*.cs` with the resource embedded.
- **Open**: lakes cut the road network (no shore roads; the Rhône road ends at Lake Geneva), roads
  never cross watersheds (no passes), and villages are named "Village <id>".
