# Collision commits: one piece per frame, nearest first

## Rule

- Never build a physics shape for a tile inside `CommitReadyResults`. A build result only
  *queues* its collision on the tile's `ChunkState` (`QueuedHeight` + `HeightCellsQueued`,
  `QueuedRoadCells` + `RoadCellsQueued`, `QueuedBuildingCells` + `BuildingCellsQueued`) and adds the tile to
  `_collisionQueue`;
  `ChunkManager.CommitCollisionPieces` commits the pieces, at least one a frame and more only
  while the frame's `CommitBudgetMs` lasts, the piece nearest a collision anchor first.
- The ground is 16 `HeightMapShape3D` cells of 251² (`ChunkNode.SetCollisionCell`, cell order
  `ChunkNode.CollisionCell(col,row)`), never one 1001² shape. Building faces are sorted into the
  same 16 cells on the worker (`ChunkNode.SplitByCell`) and committed per cell
  (`SetBuildingCell`), all under the one `BuildingBody`; so are the road faces (bridge decks,
  walls, railings, islands, sign poles, kerbs: `SetRoadCell`, under the one `RoadBody`).
- A new per-tile collision layer (walls, railings, props, anything with a `ConcavePolygonShape3D`)
  either joins the road faces (`bridgeCollision` in `StartBuild`, already split by cell) or becomes
  its own cell set: `Queued…Cells` + `…CellsQueued` on `ChunkState` (include it in
  `CollisionQueued`), a piece id range next to `PieceRoads`/`PieceBuildings`, a `Consider(...)`
  line with its tie-break, a branch in the commit `if`, and a name in `PieceKinds` (the `kind`
  column of `commits.csv`). Always split by `SplitByCell` on the worker.
- Ground a body can stand on (height cells, road cells) gates `HasCollisionAt`: bodies wait on
  it before they are placed or move (`FootPlayer`, `VehicleBody`, probes).

## Why

#221 (feat/221-terrain-collision). Before, a tile's collision landed in one main-thread frame: a 1001²
`HeightMapShape3D` (~80 ms), the buildings' BVH (up to 80 ms) and the bridges together.
Measured with `--ride` + `--perflog` in Riddes, Medium, back to back against main:

| | before | after |
|---|---|---|
| worst single commit | 110-118 ms | 15 ms (`coll-bldg` 14.5, `coll-height` <= 7.9) |
| frames > 33 ms, foot 90 s (3 runs) / car 60 s | 23-24 / 23 | 4-6 / 7 |
| commit hitches in events.log | 13-17 | 0-1 |
| builds.csv ground / complete p50, p95 | 17 / 24, 116 / 117 ms | the same |

## Same logic, preserved

- What collides is unchanged: same map, same faces, same bodies, only more shapes per body
  (`BuildingBody` is still the one body doors make exceptions for; `RoadBody` keeps its name for
  `TunnelProbe` and its `BackfaceCollision`).
- `HasCollision` (tile) still means "all the ground is there" (`PlayableNear`, the loading
  screen); `HasCollisionAt` now asks for the **cell** under the point (ground and road), so a
  body is placed as soon as its own cell lands.
- A rebuild never opens a hole: `HeightCellsDone` bits are never cleared, the old cell's shape
  stays until its replacement is added. A newer result overwrites a queued older one, so an
  interim map never lands after the blended one.
- `needCollision` also checks `!CollisionQueued`, or the ring evaluator would start a second
  collision build for a tile whose pieces are still waiting.
- `OfflineMode` (video export) commits every piece at once.
- Trap: committing a piece outside `CommitCollisionPieces` (e.g. straight from a result) brings
  the 80 ms frame back and skips the `commits.csv` kind.

## Migrating old code / open branches

- grep `SetCollision(`, `SetBuildingCollision(`, `SetRoadCollision(` — all gone; the queue calls
  `SetCollisionCell` / `SetRoadCell` / `SetBuildingCell`, never call them directly.
  `BuildResult.BuildingFaces` and `RoadCollisionFaces` are now `Vector3[][]` (16 cells): a branch
  adding faces to either adds them inside the `SplitByCell(...)` call.
- grep `CollisionCommitsPerFrame`, `collisionBudget` — gone; the budget is `CommitBudgetMs`.
- grep `CommitLogged` — its third argument is now a `string kind` ("ground", "tail",
  "coll-height", "coll-road", "coll-bldg"), not `bool interim`; `PerfRecorder.OnCommit` follows.
- #229 deletes `ChunkNode.ClearRoads/ClearCollision` around the old `SetCollision`: on conflict,
  keep this branch's `SetCollisionCell` block and drop both dead methods.

## How to check

- `--ride foot,90 --at 2583900,1113500 --heading 90 --perflog 200` and
  `--ride car,60 --at 2583250,1113250 --heading 65 --perflog 200` (both must print the RESULT
  "stayed on the surface"): `commits.csv` rows of kind `coll-*` stay under ~10 ms, and
  `summary.txt` MAIN-THREAD COMMITS lists no `coll-*` piece among the slow ones.
- `--voidcheck` still passes.
