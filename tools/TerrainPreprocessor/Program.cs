using UnitSport.Tools.Preprocessor;

// swissALTI3D XYZ zips -> .terr chunk files + manifest.json
// Usage:
//   dotnet run --project tools/TerrainPreprocessor -c Release -- --in <dir> [--in <dir> ...] --out terrain_chunks
//       [--temp <cache dir>] [--jobs N] [--io-jobs N] [--force] [--fresh] [--verify] [--dump-png <dir>]
//   ... --out terrain_chunks --photos [--tiles-file f] [--io-jobs N] [--force]   (SWISSIMAGE, PhotoStage)
//   ... --out terrain_chunks --fields ressources/data/lwb [--osm-pbf f] [--gwr data.sqlite] [--tiles-file f]   (farm fields, FieldStage)
// --in is searched recursively and may be repeated (sources can live on any drive); the build is
// incremental — see TerrainBuild.
//
// Every flag and every message lives in Preprocessor.RunAsync now, so the game can drive the same
// pipeline in-process (no .NET SDK, no subprocess) with progress and cancellation; this is a thin
// wrapper over it, kept so `dotnet run` and MapSetup's subprocess still work exactly as before.
return await Preprocessor.RunAsync(args);
