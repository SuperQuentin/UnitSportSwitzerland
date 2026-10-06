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
  (72% taken, below 1700 m, floor >= 70 m); side-street houses get a garage beside them half the
  time (`vehicles/garage-buildings`). 2077 rivers, ~1400 lines, ~7000 villages over the map.
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
  - **A bed above the floor floated its water** (#572). The floor follows each valley's axis,
    the channel meanders, so along a bend the bed is read further upstream than the floor beside
    it: up to 10 m above it, never dug, and `Alt < Bed + 0.5` (one-sided) drew the water there,
    hanging over the ground (8,644 such 2 m samples in 25x25 tiles; a camera at ground + 2.5 m
    in such a stretch was *under* it and saw "water in the sky"). The flat bottom is now
    `ChannelBed` = min(bed, the ground before the channel), so the river runs on the floor with
    its channel dug into it (left as it was where a lake reaches, so mouths meet the shelf).
  - **Narrow rivers came out as dotted patches** (#572): the cover classifies cells near a channel
    from 10 m fields mixed bilinearly, and a distance to a line is V-shaped: mixed from the corners
    it reads up to 7.1 m (half the square's diagonal) too far, so a channel narrower than a square
    was water only round the corners that sat near its line (40% of a 8 m stream drawn). In the
    band where that error could matter the cover asks `InWetChannel` exactly (any wet channel,
    since at a confluence a stream's line runs inside the wide river and is the nearer one).
    `BuildWater` takes the lower of the exact bed and the nearest 5 m point's, and lets ground up
    to 25 cm over a river's level count (lifted to it): between 5 m points a steep stream's ground
    is mixed from up and down its bed. Cover +2.4 ms on a river tile (13.6 ms median).
    Check: `BlendCheck --generated-water [--radius N]`: 100.0% of the samples well inside a dug,
    wet channel drawn, none standing a metre deeper than the channel's profile.
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
- **Towns** (#559, `ProceduralWorld.Towns.cs`): village slots with `HalfLength > 381` (top 15%) are
  towns when the ground allows it (9 slots in the anchor's world, 6 host the layout). The valley
  road is a main street built tight +-150 m round the centre (gaps 2-7 m, half apartments, half
  shops), and a **cross road** (`Road`, 6 m, a priority road) runs straight through it: uphill 110-360
  m with houses both sides (it replaces the first side street), downhill to the first channel, over
  it on a **`RoadFlags.Bridge` segment** (bank + 6 m each side, level deck 0.5 m over the higher bank;
  the street running up to either end ramps to the deck over 30 m, never the deck down to the
  ground), then 45 m to a `Minor` street along the other bank with houses. The channel is carved
  under it because `Keep` only reacts to the valley lines, not village streets. No river within
  300 m, a dry channel, a span over 90 m, a lake or steep ground: stays a village. Every street keeps
  its `gen-village-<id>-<k>` key. Houses keep 10 m (8 m) off a street's axis and off each other, trees
  stay 7 m off a town's streets' edge. Harness: `GeneratedRoadsSpike --towns` (+ `--reach`) writes a
  plan-view SVG per town; 90 of 90 tiles byte-identical, 1-3 lights a town.
- **Open**: lakes cut the road network (no shore roads; the Rhône road ends at Lake Geneva), roads
  never cross watersheds (no passes), and villages are named "Village <id>".
