# Collision commits: one piece per frame, nearest first

## Rule

- Never build a physics shape for a tile inside `CommitReadyResults`. A build result only
  *queues* its collision on the tile's `ChunkState` (`QueuedHeight` + `HeightCellsQueued`,
  `QueuedBridgeFaces`, `QueuedBuildingCells` + `BuildingCellsQueued`) and adds the tile to
  `_collisionQueue`;
  `ChunkManager.CommitCollisionPieces` commits the pieces, at least one a frame and more only
  while the frame's `CommitBudgetMs` lasts, the piece nearest a collision anchor first.
- The ground is 16 `HeightMapShape3D` cells of 251² (`ChunkNode.SetCollisionCell`, cell order
  `ChunkNode.CollisionCell(col,row)`), never one 1001² shape. Building faces are sorted into the
  same 16 cells on the worker (`ChunkNode.SplitByCell`) and committed per cell
  (`SetBuildingCell`), all under the one `BuildingBody`.
- A new per-tile collision layer (walls, railings, props, anything with a `ConcavePolygonShape3D`)
  is a new piece: a `Queued…` field on `ChunkState` (include it in `CollisionQueued`), a piece id
  next to `PieceBridge`/`PieceBuildings`, a `Consider(...)` line with its rect and tie-break, a
  branch in the commit `if`, and a name in `PieceKinds` (it is the `kind` column of `commits.csv`).
  A piece that can cost more than ~5 ms is split spatially like the height cells.
- Ground a body can stand on (the height cells, bridges) must gate `HasCollisionAt`: bodies wait
  on it before they are placed or move (`FootPlayer`, `VehicleBody`, probes).

## Why

PR #TBD (#221). Before, a tile's collision landed in one main-thread frame: a 1001²
`HeightMapShape3D` (~80 ms), the buildings' BVH (up to 80 ms) and the bridges together.
TBD before/after table (worst commit ms, frames > 33 ms, commit hitches, builds.csv complete ms).

## Same logic, preserved

- What collides is unchanged: same map, same faces, same bodies (`BuildingBody` is still the one
  body doors make exceptions for; bridges keep `BackfaceCollision`).
- `HasCollision` (tile) still means "all the ground is there" (`PlayableNear`, the loading
  screen); `HasCollisionAt` now asks for the **cell** under the point plus the tile's bridges, so
  a body is placed as soon as its own cell lands.
- A rebuild never opens a hole: `HeightCellsDone` bits are never cleared, the old cell's shape
  stays until its replacement is added. A newer result overwrites a queued older one, so an
  interim map never lands after the blended one.
- `needCollision` also checks `!CollisionQueued`, or the ring evaluator would start a second
  collision build for a tile whose pieces are still waiting.
- `OfflineMode` (video export) commits every piece at once.
- Trap: committing a piece outside `CommitCollisionPieces` (e.g. straight from a result) brings
  the 80 ms frame back and skips the `commits.csv` kind.

## Migrating old code / open branches

- grep `SetCollision(` and `SetBuildingCollision(` — both gone; the queue calls
  `SetCollisionCell(map, cell)` / `SetBuildingCell(faces, cell)`, never call them directly.
  `BuildResult.BuildingFaces` is now `Vector3[][]` (16 cells): wrap new faces in `SplitByCell`.
- grep `CollisionCommitsPerFrame`, `collisionBudget` — gone; the budget is `CommitBudgetMs`.
- grep `CommitLogged` — its third argument is now a `string kind` ("ground", "tail",
  "coll-height", "coll-bridge", "coll-bldg"), not `bool interim`; `PerfRecorder.OnCommit` follows.
- A branch that adds faces to `RoadCollisionFaces` (#230 adds walls, railings, islands, sign
  poles to the bridge faces) needs nothing: they ride the bridge piece. If that piece gets
  expensive (commits.csv `coll-bridge` > ~5 ms), give it its own piece.
- #229 deletes `ChunkNode.ClearRoads/ClearCollision` around the old `SetCollision`: on conflict,
  keep this branch's `SetCollisionCell` block and drop both dead methods.

## How to check

- `--ride foot,90 --at 2583900,1113500 --heading 90 --perflog 200` and
  `--ride car,60 --at 2583250,1113250 --heading 65 --perflog 200` (both must print the RESULT
  "stayed on the surface"): `commits.csv` rows of kind `coll-*` stay under ~10 ms, and
  `summary.txt` MAIN-THREAD COMMITS lists no `coll-*` piece among the slow ones.
- `--voidcheck` still passes.
