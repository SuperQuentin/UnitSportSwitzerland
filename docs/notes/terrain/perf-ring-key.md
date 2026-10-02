# Ring evaluation key: compared, not built as a string (#221)

## Rule
- `ChunkManager.EvaluateRings` (10 Hz, every client and the server) asks `RingKeyChanged()`: each
  anchor's tile + collision flag in a reused list, the `LodPolicy` (by reference), `_worldVersion`
  and `BuildMeshes`, compared field by field. Do not build a string key per evaluation.
- A new input to the desired set goes in `RingKeyChanged` (the list tuple or the `rest` tuple).
- To force a recompute, set `_desiredKeyRest = default` (as `ResetAll` does).

## Why
A `StringBuilder` and its string every 0.1 s per process (and `TileId.ToString` per anchor).

## Same logic, preserved
- Recompute exactly when an anchor changes tile, gains/loses collision, the LOD policy object is
  replaced, the world version moves or meshes are switched. The old key compared `Lod.GetHashCode()`
  (reference identity) as text; this compares the reference itself.

## Migrating old code / open branches
- `grep -n "_desiredKey = \"\"" src/Terrain` on an old branch: replace with `_desiredKeyRest = default;`.
  #269 deletes `ResetAll`: in that conflict, take the deletion.

## How to check
`tools/test.sh quick` (`--origincheck` reloads rings); terrain streams while walking on a client.
