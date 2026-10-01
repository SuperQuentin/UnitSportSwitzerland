# Road segment helpers: RoadSegment.Lv95 and RoadProfiles.For (#221)

## Rule

- Tile-local road points to LV95: `var (e, n) = segment.Lv95(tileId, i);` (TerrainFormat,
  `RoadSegment`). Never re-write `MinE + Points[i * 3]` / `MaxN - Points[i * 3 + 2]` for a road segment.
- RoadGen: the profile of a `.road` segment comes from `RoadProfiles.For(segment, dividedScale)`
  (`tools/RoadGen/Network/RoadProfile.cs`); the class table alone is `RoadProfiles.ForClass(c)`.
  No private `ProfileFor` copies.

## Why

The unpack was written four times (`src/Gpx/RoadNetwork.cs`, `src/World/LaneGraph.cs`,
`RoadTileImporter`, `TileRewriter`) and the class-to-profile table twice; the copies had already
drifted (see below). Pure consolidation, no runtime cost change. PR #PRNUM.

## Same logic, preserved

- `Lv95` is the same double arithmetic (`TileId.MinE/MaxN` are doubles, the float point widens);
  the altitude `Points[i * 3 + 1]` is still read by the caller.
- The two `ProfileFor` copies were NOT identical and both behaviours are kept:
  - importer (`RoadTileImporter`): `RoadProfiles.For(segment, scale)`: surveyed width (> 0.1 m)
    else the class width, times `dividedScale` if `Divided`, and an unpaved surface sets
    `Paved = false` and `Markings = None` (a paved track becomes paved).
  - rewriter (`TileRewriter`): `RoadProfiles.For(segment, scale, surfaceDecidesPaving: false)`: same
    width, but `Paved`/`Markings` stay the class's whatever the surface.
  - **Open question for the RoadGen owner**: should the rewriter follow the surface too? Changing it
    changes the rewritten `.road` tiles (markings on gravel roads), so it was not "fixed" here.
- Tests: `tests/UnitSportSwitzerland.Tests/RoadSegmentTests.cs` pins both paths.

## Migrating old code / open branches

- `grep -rn "MinE + .*Points\[i \* 3\]\|MaxN - .*Points\[i \* 3 + 2\]" src tools` -> `segment.Lv95(id, i)`.
  (`tools/BlendCheck/Program.cs` steps `i` by 3 over raw points: left as is.)
- `grep -rn "ProfileFor" tools/RoadGen` -> `RoadProfiles.For(...)`, choosing `surfaceDecidesPaving`
  to match what that caller did before.
- `src/World/LaneGraph.cs` is also changed by `feat/159-races-in-traffic-2` (not on these lines):
  on a conflict keep the branch's code and the one-line `var (e, n) = seg.Lv95(tile.Id, i);`.
- PR #229 (`feat/221-dead-code`) also edits `src/Gpx/RoadNetwork.cs` (removes `NodeCount`, other hunk).

## How to check

`tools/test.sh unit`: `RoadSegmentTests`.
