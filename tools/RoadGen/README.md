# RoadGen

Fluid procedural road geometry with real junctions. Standalone: no Godot, no terrain, no GDAL.

```bash
dotnet run --project tools/RoadGen -c Release -- --demo --out roadgen_out
```

Writes an SVG plan view per case and a quality report to stdout. Open the SVG in any browser.

## What it fixes

The existing pipeline turns each swissTLM3D polyline into a ribbon independently. That has
three consequences, and this tool addresses each of them:

| symptom | cause | fix here |
|---|---|---|
| junctions z-fight, exit ramps look wrong | nothing knows four ribbons meet at a point, so all four are drawn full length and overlap | a road **network graph**; junctions are explicit polygons and roads stop at their boundary |
| corners are faceted, roads look polygonal | the centreline is the surveyed polyline densified to 4 m | **clothoid** (spiral-arc-spiral) corner fitting, then curvature-adaptive tessellation |
| lane lines cross each other inside intersections | markings are a shader stripe drawn from a lateral UV along the full ribbon | markings are **offset curves** generated only between the junction trims |

The junction decision is the same one ASAM OpenDRIVE reaches: connecting roads inside a
junction are singled out as the only roads in that entire standard whose surfaces may overlap.
Everywhere else roads meet a junction boundary and stop.

## Measured

`--demo` reports every case against a baseline pass that reproduces the current renderer
(raw polylines, bisector offsets, no junctions) on the same input:

| scene | overlapping carriageway | sharpest turn between segments |
|---|---|---|
| crossroads | 45.4 m² → **0** | 13.7° → 3.4° |
| village (T junctions + a crossing) | 63.8 m² → **0** | 8.5° → 3.0° |
| motorway exit | 300.6 m² → 116.1 m² | 2.3° → 1.7° |
| alpine hairpins | none either way | 93.5° → **7.9°** |
| synthesised town, 298 links | 1,676.8 m² → **4.3 m²** | 86.5° → 25.4° |

The synthesised town also emits **11.9k ribbon vertices against the baseline's 28k** — adaptive
tessellation spends vertices where there is curvature instead of every 4 m regardless.

The motorway exit keeps overlap on purpose. Two arms diverging at 6° have their edge
intersection over a hundred metres out; trimming to it would delete the ramp. Those arms are
clamped, drawn in red in the SVG, and reported as `arms clamped` — a gore area is physically
merged pavement, not a junction.

## Verification

Every run checks its own invariants, and exits non-zero if any fail — overlap alone is not a
sufficient measure, since trimming every road to nothing would score a perfect zero.

```
checks
  ok   junction/ribbon seam   worst gap 0.00 mm
  ok   tessellation           worst chord error 43.3 mm (budget 50 mm)
  ok   alignment endpoints    worst drift 0.00 mm
  ok   junction triangles     0 degenerate
```

These caught three real bugs during development: a 320 mm tessellation error from reading
curvature at the *start* of a clothoid (where it is zero), a 6 m error from a step landing
exactly on a piece boundary, and a **271 m** alignment drift from skipping a corner too sharp
to round without emitting the sharp vertex.

## On real swissTLM3D data

```bash
dotnet run --project tools/RoadGen -c Release -- --tiles "2583,1113;2584,1113" --chunks terrain_chunks
```

Load several adjacent tiles at once. Roads are clipped at tile boundaries, so a junction on a
boundary is split across two files and loading tiles singly sees two dead ends.

Four tiles around Riddes, 690 segments: the tool's own junction defects come to 187 m² against
235,000 m² of carriageway. What remains is **not** a junction problem, and the report says so:

```
road/road 21,342.8 (of which 10,904.8 away from any node)
worst pairs: motorway+motorway 15,787 m², railway+railway 5,266 m²
```

Those two pairs are 98.6% of it. swissTLM3D draws a direction-separated road as **two**
centrelines, one per carriageway, and the preprocessor gives each of them the full class width
(`RoadFormat.DefaultWidth(Motorway)` = 11 m) — so both halves of the A9 are drawn 11 m wide and
painted over each other, and parallel rail tracks likewise. `--divided-scale 0.6` takes
motorway self-overlap from 15,787 to 7,342 m². It defaults to 1.0: this is a width-model
decision that changes how the whole world looks, so the tool measures it rather than making it.

## Synthesising a network

```bash
dotnet run --project tools/RoadGen -c Release -- --synth --seed 3 --size 1600
```

Tensor fields, following Chen et al.'s street-modelling method. A symmetric traceless tensor
encodes its direction as a *double* angle, which is what makes the field blendable — a plain
direction field cannot be averaged, because 0° and 180° are the same street direction but
cancel when summed. Each tensor carries two orthogonal eigenvectors at once, which is exactly
a street grid: avenues one way, cross streets the other.

Basis fields blend by weighted sum: `GridField` (a district at one orientation), `RadialField`
(a centre with ring roads), and `TerrainField`, which aligns streets to the contour and scales
its own weight by slope — so on flat ground the grid wins and on a steep face the layout turns
into switchbacks by itself. That is the one basis field a Swiss terrain project actually wants.

Streets are traced as hyperstreamlines at three decreasing separations, and a trace that
wanders within reach of an existing street is terminated *on* it. That snap is what yields a
connected graph rather than a pile of near-misses — the shared point becomes a node, and the
road it landed on is split into a T junction by the same code that handles surveyed data.

## Pipeline order

Non-obvious and load-bearing:

1. graph — snap endpoints, node true crossings, split T junctions
2. **smooth** — clothoid fit per link
3. junction trims — computed from the *smoothed* headings
4. ribbons and markings between the trims

Trims must come after smoothing. Rounding a corner changes the direction a road leaves a node
by exactly the amount that was rounded; trim first and you cut back along a heading the road no
longer has, leaving a wedge of bare ground beside every arm.

Bridges and tunnels carry a layer, and links on different layers never share a node, are never
split against each other, and are never counted as overlapping. Without that, a viaduct and the
lane beneath it share endpoints wherever their plan-view geometry touches, and the solver
builds a crossroads in mid-air.

## Getting it into the game

```bash
dotnet run --project tools/RoadGen -c Release -- --rewrite --chunks terrain_chunks
```

Rewrites the built `.road` tiles in place as format v2: carriageways trimmed back from their
junctions, junction polygons added, centrelines smoothed. `RoadMeshBuilder` draws the caps.
16 tiles around Riddes take 27 s; add `--dry-run` to measure without writing, or `--tiles` to
limit which.

Deliberately a post-process rather than a change to `RoadExtractor`. That extractor carries a
lot of measured behaviour — approach ramping onto bridge decks, tunnel carve masks, per-class
gradient limits, structure-end detection — and re-deriving centrelines inside it would put all
of it at risk for a defect that lives entirely in the plan view. Running afterwards also means a
change can be tried against a built region in seconds instead of a fourteen-minute rebuild.

Four things make that safe:

- **Heights are never recomputed.** Each output vertex takes its altitude from the original
  polyline at the nearest plan-view point. The originals already carry the drape, the surveyed
  bridge deck, the tunnel's own Z and the approach ramps blended into the abutments; re-draping
  would throw all of that away and drop every viaduct into its gorge. Smoothing moves a line by
  less than the simplify tolerance, so reading the original's height there keeps every one of
  those decisions intact.
- **Bridges and tunnels are not smoothed.** Their plan-view line is load-bearing elsewhere — the
  carve mask and the piers were both derived from it — so their shape is frozen. They still take
  junction trims; only the geometry is fixed.
- **A second run is refused.** The pass is not idempotent, and the second run is destructive in
  a way that is not obvious: the roads are already trimmed, so the new trims come out near zero
  and the tiny new caps *replace* the full-size ones, leaving a hole at every junction. It
  aborts unless `--force`.
- **Every run audits itself against the terrain.** See below.

v1 tiles still decode, so an unrewritten region keeps working and renders exactly as before.

Measured on 16 real tiles: 2,142 segments, 975 junctions, and **673,358 primitives against the
original 677,950** — adaptive tessellation saves more than the junction caps cost.

### The height audit

Carrying heights over from the original line is the one part of this that could go quietly
wrong, so it is measured rather than assumed. An output vertex at `p` takes its height from the
nearest point `q` on the original, so the error introduced is exactly `|terrain(p) − terrain(q)|`
— how much the ground differs between where the road now is and where its height came from.
Nothing else needs modelling, and comparing absolute road-to-ground distances instead is
meaningless on a cliff, where the two points sit on terrain tens of metres apart.

Over the whole region — **9.3 million samples**:

| | |
|---|---|
| mean | **8 mm** |
| p99 | **9 cm** |
| worst, before the guard | 12.14 m |
| worst, after | **1.00 m** |

The tail is entirely footpaths surveyed on cliff lips, where swissALTI3D really does drop tens
of metres between adjacent cells, so half a metre sideways is metres down. Two things bound it:

- **Smoothing may never move a road outside its own width.** `CurveStyle.MaxOffset` limits the
  external distance of every corner curve to half the carriageway width, so a 1.1 m footpath
  gets a 0.55 m budget and an 11 m motorway 5.5 m — the right way round, because the footpath
  is the one on the cliff.
- **A cliff guard puts the rest back.** Where the height change would still exceed
  `--max-height-shift` (1 m), that vertex is snapped onto the surveyed line, where its height
  genuinely belongs. Region-wide this fires on **230 vertices out of 9.3 million**. It has to
  live in the rewriter rather than the geometry engine, because the engine is deliberately
  terrain-free — only the rewriter holds the smoothed line and the heightfield at once.

Whole region: 6,487 tiles, 366,105 segments, **156,384 junctions**, in under a minute.

## Not done

- `TerrainPreprocessor` still writes v1, so `--roads-only` has to be followed by `--rewrite`.
- Roundabouts are not recognised as a shape; a small ring of links becomes several ordinary
  junctions rather than one island.
- No lane-level topology (which arm connects to which), so `MarkingPlan` infers lines from width
  and cannot place turn arrows or lane drops.
- `--divided-scale` defaults to 1.0. swissTLM3D draws a direction-separated road as two
  centrelines and the preprocessor gives each the full class width, which is where nearly all
  the remaining carriageway overlap comes from — but narrowing every dual carriageway changes
  how the whole world looks, so the tool measures it and leaves the call open.
- Roundabouts are not recognised as a shape; a small ring of links becomes several ordinary
  junctions.
- No lane-level topology (turn lanes, which arm connects to which), so `MarkingPlan` infers
  lines from width and cannot place turn arrows or lane drops.
