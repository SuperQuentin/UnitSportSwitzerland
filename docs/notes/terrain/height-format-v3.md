# Tile height format v3 (#1399)

- **Why**: v2 stores heights as uint16 over 0..4700 m. The French side of Mont Blanc (4,779 m in RGE ALTI)
  would clip at 4,700, and land below sea level (Camargue -1.1 m, the Wateringues) would clamp to 0.
- **v3**: the same `.terr`/`.terrc` file (32-byte header, 1001x1001 uint16, same size). Only the
  version word (offset 4) is 3 and the quantization is `m = -50 + q * 4950/65535`: **-50..4900 m,
  7.55 cm a step** (v2: 7.17 cm). `q = 65535` is 4900 m, which nothing in France reaches, so
  `XyzParser.MissingCell` stays a safe sentinel. `q = 0` is -50 m, so the "0 = no water / no data"
  convention of `.water` levels and landings holds (there is no water at -50 m).
- **One encoding per region**: `ChunkFormat.Current` (`HeightEncoding.Legacy` v2 or `.Wide` v3) turns
  `Quantize`/`Dequantize` (about 60 call sites, none with a tile in hand) into the region's metres. It is chosen by
  (1) `ChunkFormat.Use(v)`: the manifest loads (`TerrainManifest.UseFormat()`, called by `LocalChunkSource` and
  both `ClientTerrainSync` manifest loads) and the preprocessor's `--height-format wide|legacy`; else
  (2) the first tile decoded by `ChunkCodec.Decode(stream)` (`ChunkFormat.Adopt`). A tile of the other
  version afterwards throws "Tile is height format vN but this process decodes vM" instead of being wrong
  by metres. Default: v2.
- **Both versions read**: `ChunkCodec.Decode(stream, out HeightEncoding)` takes either version and touches no
  global state (tests, tools that look at one tile); `Encode(grid, stream, encoding)` writes either. A Swiss
  build keeps its v2 tiles and manifest (`formatVersion` 2, no `heightOffsetM`, read as 0): nothing to
  rebuild. A v3 manifest says `formatVersion` 3, `heightScale`, `heightOffsetM` -50.
- **Mixing is refused**: `TerrainBuild` rebuilds a tile whose header is not the run's version, and the
  preprocessor stops when `--out` already holds the other version (use a new `--out`).
- **Not affected**: shaders (no height is decoded on the GPU: the mesh builder passes float metres);
  the network (a tile travels as its file bytes and is decoded by the same codec on the client, which takes
  the encoding from the manifest it fetches first); the `HorizonFormat`/`WaterFormat` samples use the same
  encoding, so a v3 region's `horizon.bin` and `.water` are v3 too, their own version words unchanged.
  `PisteLayer` and `piste_snow.gdshaderinc` normalise an **altitude** by 4700 for snow quality; it is not a
  tile height and no French ski area is above it.
- **Gap to know**: a zero-filled `ushort[]` means 0 m in v2 but -50 m in v3. Nothing relies on it (water
  uses 0 as "none" on purpose).
- Tests: `TerrainFormatTests` (`Legacy_encoding_decodes_exactly_as_before_the_wide_format`,
  `Wide_encoding_holds_mont_blanc_and_land_below_sea_level`, `Wide_chunk_round_trips_and_is_told_apart_from_a_legacy_one`,
  `Manifest_names_its_height_encoding`).
