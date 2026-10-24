# Nothing that moves rebuilds its mesh (#1202)

## Rule
A mesh is built when what it **is** changes (a look, a new part, a new tile), never because it
**moves**. In `_Process`, `_PhysicsProcess`, a `Step`/`Animate*`/`Apply*Pose`/`Update*` method, a
property set every frame or anything they call: no `MeshScratch.Build`/`BuildInto`, no
`new ArrayMesh`, no `SurfaceTool.Commit`, no `ClearSurfaces` + `AddSurfaceFromArrays`, no
`ImmediateMesh` redraw, no per-element interop loop (a `SetInstanceColor` per pixel). Pick by
what the motion is:

| What moves | Do this | Examples |
|---|---|---|
| A human figure (gait, arms, dance, seat, swim, ragdoll, skirt) | a `SkinnedFigure`: `Show(look)` once, a `HumanMeshBuilder.Pose…` per change (`avatar/skinned-figures`) | walker, drivers, cyclist, skier, performers |
| A rigid part that turns, slides or tilts | its own node, transformed; a hinge is a pivot node | wheels, cranks, steering wheel, needles, doors, the forklift mast |
| A part that stretches or leans with one value | a node with a scale or a shear: the transform is affine, not only rotation | airstairs columns and flight, the steamer's gangway plank, the zipline ghost's cable |
| Many copies of one part | one mesh, drawn twice or as a `MultiMesh` | the zipline ghost's two posts, treads, crowds |
| Cloth, waves, sway, a flutter, a pulse | a vertex shader term driven by a uniform (instance uniforms only on change) | `skirt_flutter`, `skirt_floor` in `avatar.gdshaderinc` |
| A pattern, a screen, a blink | a fragment shader from `TIME`, `INSTANCE_ID`, UV and a few uniforms | (#1203: the concert LED wall, the throw arc) |
| A shape that needs CPU geometry anyway (a new road, a terrain edit) | build on a worker, commit on change, never per frame | `ChunkManager`, farm fields |

- **A quantised mesh cache is a last resort**, for a value with few steps (a crank's 128). Never
  key one on a continuous value (the steamer's plank cached a mesh per centimetre of bobbing, in a
  static dictionary that never shrank), and bound it.
- **Skip work when nothing changed**: a pose key, `IsEqualApprox` before a transform, a uniform
  only when its value changes. Nothing visible: nothing done (`FishingVisuals` cleared its
  `ImmediateMesh` every frame with no line out).
- **What measures a mesh must see the moved one**: a skinned mesh's own AABB is its rest pose
  (`SkinnedFigure.BoundsOf`/`MeshOf`), and a turned node's box round its box is wider than the
  part (`MeshBounds` measures turned parts by their vertices).

## Why
Measured (#1202): 24 walking bots round a windowed client on the fixture world (`tools/botperf.sh`,
RTX 4070 / Ryzen 2700X, two runs each). Before, each figure rebuilt its whole mesh every frame:
**131 ms a frame (7.7 fps), ~1075 gen0 and 335 gen1 collections in 90 s**; the client's trace is
`ApplyFootPose -> BuildStride`, two thirds building the figure, a third uploading it. With skinned
figures: **18-19 ms (53-54 fps), ~40 and ~25**; posing is the bone calls to the rendering server.
On the Quest (#1112) the frame rate quadrupled when the tracked hands stopped the own figure's
arms from moving, which was the first sign.

## How to check
- **`MeshRebuildDisciplineTests`** (tier 0) fails on a mesh build in a per-frame method (`_Process`,
  `_PhysicsProcess`, `Step…`, `Animate…`, `Apply…Pose`, `Steer…`) or a same-class method it calls;
  an exception goes in its `Allowed` list as `file:Method` with a reason (`on change:`, `cache:`,
  `tool:`, `live:`), and a stale one fails too. It cannot see a build behind a call into another
  class or in a property set every frame (the airstairs' `Height`): the rule holds there anyway.
  Run against the code before #1202 it named the walker, swimmer, ragdoll, cyclist, car and truck
  drivers, motorbike rider and concert performers.
- `--perflog` (`core/session-perf-log`): `gc0` per minute and frame time with the thing moving in view.
- `tools/botperf.sh N` for figures: frame time and GC with N bots walking round the client.
- Before a new animated thing lands, grep its per-frame path:
  `grep -nE "Build\(|BuildInto\(|new ArrayMesh|ClearSurfaces|SurfaceBegin|Commit\(" <file>`.
