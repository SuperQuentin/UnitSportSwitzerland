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
caller, so each miss scanned every point of the tile. `--surfacecheck` on 5 Mollendruz tiles (600-1642
pieces): a full scan cost 12-175 µs per lookup, a cell lookup 0.3-6 µs (×30-75), with 0 differences in
17,380 lookups. That is per driven vehicle per tick at speed (PR PRNUM).

## Same logic, preserved
- The answer is the same as the old full scan, ties included. A piece is listed in every cell its
  box touches, grown by its reach (`Width/2 + 0.3`). Cells list the pieces in (segment, point) order,
  so the first piece found still wins a tie.
- Trap: a rule that widens the reach (a larger tolerance, a verge) must widen it in the
  `RoadIndex` constructor too, or cells silently miss pieces. A segment filter (like the
  `Embedded` one from #230) belongs in `Walkable`, which runs before indexing.
- The cache (0.25 s, 0.5 m) is unchanged, now per caller.

## Migrating old code / open branches
- `grep -n "Surfaces.At(" src`. A call for a vehicle gets the vehicle as the 4th argument.
  PR #169 (`feat/162-walk-in-bus`) adds `Audio.Surfaces.At(Terrain, GlobalPosition, inside)` in
  `VehicleBody.StepDriverless`: make it `(..., inside, this)`.
- A branch that still has the old `RoadUnder` loop over `segs` / `s.Points`: take main's version
  whole (`RoadIndex`, `Load`, `Evict`, `Watch`) and re-apply only its own edits to `Walkable` or to
  the surface choice in `RoadIndex.Nearest`.
- `Roads` is now `Dictionary<TileId, RoadIndex?>`. Code that read it as `List<RoadSegment>` goes
  through `RoadIndex`.

## How to check
Run any session with `--surfacecheck`. For example `--drivecheck --at 2518038,1167321 --setups 1
--originshift 1000000 --surfacecheck`. Each road tile read prints
`[surfacecheck] tile ...: N lookups ..., 0 differ from the full scan RESULT ok`.
