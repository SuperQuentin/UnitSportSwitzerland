# Dead code and shared helpers (#221, PR #229)

## Rule

- WGS84 <-> LV95: call `UnitSport.Terrain.Format.SwissProjection.ToLv95/ToWgs84` (in
  `tools/TerrainFormat`). `UnitSport.Gpx.SwissProjection` no longer exists; never re-add a forwarder.
- Tiles files (`--tiles-file`, "E-N" per line, `#` comments): `TileId.ReadList(path)` (returns a
  `List<TileId>`; `.ToHashSet()` when you need a set). Never write another `ReadTilesFile`.
- Before adding a helper, grep for one; before deleting a member, prove it is unused (below).

## Why

#221 found these members referenced only by their own declaration and removed them in #229
(~130 lines): `CdInfo.BeatPeriod`, `EngineSynth.Player3D`, `CinemaEvent.IsScenery/IsEffort/IsTerrain`,
`Gpx.RoadNetwork.NodeCount`, `Runner.StridePhase`, the `JoinedAt` of a `PlayerRegistry` entry,
`HeavyCatalog.IsHeavy`, `HeavyDriveline.Shifting`, `ChunkManager.GetHeightAt`,
`ChunkNode.ClearRoads/ClearCollision`, `NetworkChunkSource.CacheBytes/ClearCache`, `RaceNpc.InZone`,
MapSetup `Paths.BuildingsGpkg`/`Selection.WriteTilesFile`, RoadGen
`Alignment.SamplePoints`/`RoadNetwork.UsableLength`, `ChunkFormat.FlagDeflate`. Nothing measured: cleanup only.

## Same logic, preserved

- `TileId.ReadList` parses exactly like the 3 old copies (`Split('#')`, trim, `Split('-', '_', ',')`,
  `int.Parse`).
- Alive although they look dead, keep them: `ProceduralWorld.SingleLevelForChecks` (BlendCheck sets
  it), `RealWeight`, `TerrainManifest.FormatVersion`, `Truck._remoteRpm` (drives the remote cockpit).
- Not removed yet because open PRs were editing their files: `ItemIcons.IsAuthored`,
  `PhotoUi.AlbumOpen`, `ChatManager.StreamStatus`, `FootPlayer.DrawnArmBlend/StepPhase`,
  `Truck.RemoteRpm`. Delete them later only after re-checking.

## Checking a member is really unused

1. `grep -rnw <Name> --include=*.cs --include=*.tscn --include=*.gd --include=*.tres --include=*.sh --include=*.py --include=*.md .`
   (skip `addons/`). Only the declaration may match. Roslyn `find_dead_code` misses most cases.
2. Also look for indirect uses: `nameof(...)`, strings in interpolations or log/probe output,
   `Call("Name")`/signals from scenes, `[Export]` properties set in `.tscn`/`.tres`.
3. Tools compile game sources: `tools/BlendCheck/BlendCheck.csproj` links
   `src/Terrain/ProceduralWorld*.cs` and the chunk sources, `tools/MapSetup` links
   `TerrainPreprocessor/GeoPackageReader.cs`, and the game references `tools/TerrainFormat`.
   Check `rg -n "Compile Include" tools` and build every csproj you touched.
4. A getter over a field: remove the getter, keep the field if it is still used.
5. Check open PRs both ways: `gh pr list --json number,files` (skip files they edit) and
   `gh pr diff <n> | grep "^+" | grep -w <Name>` (an open PR may start using it).

## Migrating old code / open branches

After rebasing onto main:
- `Gpx.SwissProjection` / `SwissProjection` in a file without `using UnitSport.Terrain.Format;` ->
  add that using (or write `Terrain.Format.SwissProjection`).
- `ReadTilesFile(` in `tools/` -> `TileId.ReadList(`.
- A compile error on one of the removed members above -> your branch uses it: re-add it in your PR
  (it is no longer dead) and say so.
- No open PR used any removed member when #229 was opened (checked with `gh pr diff`).

## How to check

`dotnet build` of `UnitSportSwitzerland.csproj` and of every `tools/*/*.csproj` (`TerrainFormat`,
`MapSetup`, `RoadGen`, `TerrainPreprocessor`, `BlendCheck`); smoke `--shot` from `SETUP.md` step 3.
