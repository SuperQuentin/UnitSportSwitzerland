# Terrain LOD rings, tree density by ring, shared unit meshes

## Rule

- Pick a ring's stride by screen-space error, not by "finer looks better": a ring-d tile is at
  least (d-1) km away, an s-metre quad there spans s/(d-1) mrad, and one pixel of the 640-wide
  vertex snap is ~1.9 mrad. Past the first ring keep every ring at 3-5.5 mrad (`LodPolicy.Create`).
  Strides must still divide 1000 and, past the road ring, be multiples of `ChunkFormat.CoarseStride`.
- Trees: a tile draws `LodPolicy.TreeDensity(dist)` of its instances (1 for d<=1, 0.5 at d=2,
  0.25 beyond), through `ChunkNode.SetTreeDensity` -> `MultiMesh.VisibleInstanceCount`. The
  instances are shuffled on the worker (`ChunkNode.Pack`, seeded by the count), so a prefix is
  an even thinning. Never rebuild a tile to thin it, never thin by position.
- Unit meshes a `MultiMesh` points at are shared statics (`ChunkNode.UnitMesh(material, kind)`),
  never built per tile: a `MultiMesh` does not own its mesh, so a per-tile one leaks.
- Every `Godot.Collections.Array` handed to `AddSurfaceFromArrays` is `using` (or go through
  `ChunkNode.ToArrayMesh`); every replaced or dropped `MeshInstance3D` frees its mesh now
  (`ChunkNode.Swap`/`ReleaseResources`, `HorizonLayer.Free`), not via the finalizer.
- A new per-tile instanced layer (grass, rocks, props) follows the trees: shuffle on the worker,
  shared unit mesh, `VisibleInstanceCount` from `TreeDensity` (or its own curve in `LodPolicy`),
  applied in `ChunkNode.SetTreeDensity`/`ApplyTreeDensity`, so ring changes reach it for free.

## Why

#221 (feat/221-terrain-lod), Medium preset, 15 rings, RTX 4070 Ti, same `--shot-queue` spots:

| | before | after |
|---|---|---|
| prims, near / 1 km / aerial 1.5 km | 15.2M / 15.2M / 16.6M | 10.9M / 10.9M / 11.8M |
| prims, aerial 2.5 km / forest / Alps | 9.7M / 17.9M / 6.9M | 6.1M / 13.1M / 3.8M |
| `--fly` 100 m/s 40 s: GPU ms avg, prims avg | 2.85, 14.2M | 2.55, 10.3M |
| vram max | 1454 MB | 1287 MB |
| worker `surface` ms per tile | 4.8 | 3.9 |

The pictures are essentially identical at the game's 1152x648.

## Same logic, preserved

- Rings 0 and 1 (everything a player can reach on foot) keep strides 1 and 2; roads, buildings,
  collision distances are unchanged. Low and High presets are unchanged.
- Trees within one tile of the player are all drawn; the 3D/billboard crossfade
  (`StyleKit.TreeNear`) is unchanged; the same trees are kept on every peer and every rebuild.
- Tints still follow the original tree order, so a tile's colours do not change.
- Trap: a tile's ring changes without a rebuild; the density is re-applied in
  `RecomputeDesired` for every loaded tile and on each tree commit. A new code path that
  replaces a tile's MultiMesh must go through `SetTrees` (it re-applies the density).

## Migrating old code / open branches

- grep `ConeMesh(`, `CrownMesh(`, `BillboardMesh(` outside `UnitMesh` — replace with
  `UnitMesh(material, 0|1|2|3)`.
- grep `new Godot.Collections.Array()` without `using`, and `AddSurfaceFromArrays` outside
  `ChunkNode` — use `ChunkNode.ToArrayMesh` or add `using`.
- grep `new(2, 2), new(3, 4)` in a branch's `LodPolicy` — the Medium table is now
  `new(0, 1), new(1, 2), new(2, 4), new(6, 10), new(9, 20)`; keep this one on conflict.
- Open PRs touching the same files: #229 (dead code in ChunkNode/ChunkManager), #230 (roads:
  ChunkNode `ToArrayMesh` overloads, ChunkManager build tail). Conflicts are adjacent hunks;
  keep both sides.

## How to check

- The same `--shot-queue` before/after (near, 1 km, 3 km, forest, Alps) at `--origin
  2583000,1113000 --builds 0 --nohud`: pictures essentially identical; each shot logs prims.
- `--fly 0,1500,0,-70,100,40 --perflog 200`: `frames.csv` prims and gpu_ms down, no new hitches.
