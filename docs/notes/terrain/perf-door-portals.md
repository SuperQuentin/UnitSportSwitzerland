# Door portals, ghosts, door lights, interiors: per-frame cost

## Rule
- `DoorPortals._Process` allocates nothing: links go into `_linkList`, `Seen` fills a caller's
  list (`_direct`, `_through`) from the reused `_found`, the open-door set is compared with
  `_openSet` (no `string.Join` key), corners are a `stackalloc` span.
- A quad's `live` parameter is written only when it changes: `_live`/`_wasLive` hold the quads
  given a picture this frame and the last; quads dropped since go dark at the end of
  `_Process`. Never reset every quad to `live = false` at the top of the frame again.
- Shader and global parameter names are `static readonly StringName`s (`LiveParam`, `ViewParam`,
  `ClipEye[slot]`, `ClipPlane[slot]`, `DoorLights.Globals`).
- `DoorLights` writes a slot's global only when its value changed or the slot was off.
- `DoorwayGhosts` scans the `doorway_travellers` group at 10 Hz (or at once when the number of
  open doors changes) for things within 12 m of an open doorway; only those get the exact
  per-frame test and ghost update. A new mover faster than 40 m/s would need a wider margin.
- `InteriorManager.BuildInterior` builds the `ArrayMesh` on the worker with the arrays
  (`InteriorNode.BuildMesh`, like `ChunkNode.ToArrayMesh`), and adds the collision body
  (`InteriorNode.AddBody`) one frame after the node. `InteriorNode.Create` without a mesh
  (the `PortalDemo`) still builds both at once.

## Why
#221, `--interiorcheck --perflog` main vs branch: gen1 137 -> 38 per minute, frame p99 3.23 ->
3.03 ms. `--portaldemo` pictures: pixel-identical, or within the run-to-run noise (< 0.01 % of
pixels, the same between two runs of one build).

## Same logic, preserved
- The set of doors sent to `OpenDoors` (the building shader's hidden leaves) changes exactly
  when it did: same doors -> nothing sent. The order inside the arrays was never meaningful.
- Quads' visibility is still written every frame from `Swing`; only `live` became incremental.
- Ghosts: the exact test (`Near`, 8 m cheap test, plan match, visible in tree) is unchanged, and
  runs every frame on the candidates.
- The interior has no collision for one frame after it appears, 3 km down, before anybody can
  have walked in.

## Migrating old code / open branches
- PRs #219 and #197 touch `InteriorManager.cs` near `AddLockDoors`, not `BuildInterior` or
  `InteriorNode.Create`: a rebase should merge cleanly. Code that relied on the `Body` child
  being added before the door leaves must look it up by name.
- Grep for regressions:
  `grep -nE '\.(ToList|OrderBy|Where|Select)\(|string\.Join|SetShaderParameter\("|GlobalShaderParameterSet\(\$?"' src/Interiors/Door*.cs`
  (one-off setup in `Quad`/`_Ready` is fine).

## How to check
`--interiorcheck` (RESULT line) and `--portaldemo,<dir>/p.png` (compare the pictures with
main's); online, `--interiorcheck` on one client and `--doorwatch` on another.
