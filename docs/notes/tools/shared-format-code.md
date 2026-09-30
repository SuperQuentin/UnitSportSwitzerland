# Shared format code

- **Shared format code**: `tools/TerrainFormat` classlib (TileId, ChunkFormat, ChunkGrid,
  ChunkCodec, TerrainManifest) — referenced by both the preprocessor and the game csproj.
  The game csproj excludes `tools/**` from its wildcard compile.
