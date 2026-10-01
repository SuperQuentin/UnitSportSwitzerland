# Surface lookup: road grid and per-caller cache (#221)

## Rule
- Ask for the ground under a vehicle with `Surfaces.At(chunks, pos, indoors, this)`. Pass the
  caller (the `FootPlayer`, a `VehicleBody`) so it gets its own cache. Leave `caller` out only
  for the local player's feet (footsteps, ski hiss), which share one cache.
- Never scan the `RoadSegment.Points` of a whole tile per call. Road lookups go through the tile's
  `RoadIndex` (32 m cells, built off the main thread when the road tile loads): `Cell(lx, lz)`,
  then `Nearest(...)`.
- Do not keep road tiles elsewhere for this. `Surfaces` evicts them on `ChunkManager.TileUnloaded`
  and on `Forget()`.

## Why
Every driven motorbike, car or truck (and NPC car with a preset) calls `At` every physics tick.
Above ~2 m/s the 0.5 m cache missed every tick, and the one shared cache was overwritten by the next
caller. Each miss scanned every point of the tile. Numbers: see PR PRNUM.

## Same logic, preserved
- The answer is the same as the old full scan, ties included. A piece is listed in every cell its
  box touches, grown by its reach (`Width/2 + 0.3`). Cells list the pieces in (segment, point) order,
  so the first piece found still wins a tie.
- Trap: a rule that widens the reach (a larger tolerance, a verge) must widen it in the
  `RoadIndex` constructor too, or cells silently miss pieces. A segment filter (like PR #230's
  `Embedded`) belongs in `Walkable`, which runs before indexing.
- The cache (0.25 s, 0.5 m) is unchanged, now per caller.

## Migrating old code / open branches
- `grep -n "Surfaces.At(" src`. A call for a vehicle gets the vehicle as the 4th argument.
  PR #169 (`feat/162-walk-in-bus`) adds `Audio.Surfaces.At(Terrain, GlobalPosition, inside)` in
  `VehicleBody.StepDriverless`: make it `(..., inside, this)`.
- PR #230 (`feat/114-road-network`) edits `Walkable` (adds `!s.Attributes.Has(RoadAttrFlags.Embedded)`).
  On rebase keep main's `RoadIndex` / `Load` / `Evict` code and re-apply only the `Walkable` line.
- `Roads` is now `Dictionary<TileId, RoadIndex?>`. Code that read it as `List<RoadSegment>` goes
  through `RoadIndex`.

## How to check
Run any session with `--surfacecheck`. For example `--drivecheck --at 2518038,1167321 --setups 1
--originshift 1000000 --surfacecheck`. Each road tile read prints
`[surfacecheck] tile ...: N lookups ..., 0 differ from the full scan RESULT ok`.
