# Tile header: one TileHeader, byte-identical files (#221)

## Rule

- The 20-byte prefix of a per-tile file (u32 magic, u16 version, u16 flags, i32 E, i32 N,
  u32 count) is written with `new TileHeader(Magic, Version, flags, id, count).Write(output|span)`
  and read with `TileHeader.Read(input, Magic, "kind")` (reads 20 bytes, throws `Bad <kind> magic`)
  or `TileHeader.Read(span)` (no check). Never hand-write `BinaryPrimitives` at offsets 0..20 again.
- Version and flags checks stay per format: `header.CheckVersion(Version, "kind")`, flags by hand.
- A `.terr` header (32 bytes) is parsed only by `ChunkCodec.ReadHeader(span)` (no checks, gives
  `GridSize`, `RawStride`, `Stride`, `MinHeight`, `MaxHeight`); `ChunkCodec.Decode` validates.
  Code that peeks at a `.terr` on disk (the preprocessor's resume scan) uses `ReadHeader`, not offsets.
- A new tile format with that prefix uses `TileHeader` and gets a golden in
  `tests/UnitSportSwitzerland.Tests/TileHeaderGoldenTests.cs`.

## Why

Five hand-written copies (Cover, Hole, Tree, ChunkCodec, plus `TerrainBuild.IsValidTerr` /
`ReadHeaderTile`) of the same layout; one diverging offset corrupts every tile on disk. No runtime
cost change (same stackalloc, same writes). PR #240.

## Same logic, preserved

- Output is byte-identical: SHA-256 goldens of `.terr` (full and coarse), `.holes`, `.trees` and the
  `.cover` header were taken on the old code first, then the refactor had to pass them.
- Per-format differences kept on purpose:
  - `.cover` writes flags = 1 (deflate) and the cell side (501) in the count word; its payload is
    not pinned byte for byte (deflate output belongs to the runtime's zlib), only inflate-equality.
  - `.terr` count word = `GridSize | stride << 16` (u16 grid size, u16 stride). Stride 0 is a
    legacy file and reads as 1 (`ChunkHeader.Stride`); `RawStride` keeps the stored value.
  - `IsValidTerr` accepts `RawStride` 0 or 1 only (a coarse companion is not a built tile) and does
    not check flags; `Decode` checks magic, version, flags 0, grid size, stride divides 500.
  - `TreeFormat.Decode` checks magic only (it never checked the version); Cover and Holes check
    magic + version, not flags. Do not "tighten" these: old files on disk must keep loading.
  - Error messages are unchanged (`Bad chunk magic 0x...`, `Unsupported cover version N`, ...).
- Not migrated (different layouts or out of scope): `RoadCodec` and `BuildingCodec` share the same
  first 20 bytes but carry a second u32 (header 24 bytes); `HorizonFormat` has its own layout;
  `TileRewriter` peeks at the road header's word at 20.

## Migrating old code / open branches

- `grep -n "WriteUInt32LittleEndian(header\[0..\], \(Magic\|ChunkFormat.Magic\)" tools src` and
  `grep -n "ReadUInt16LittleEndian(h\[18..\])\|ReadSingleLittleEndian(h\[20..\])" tools`: replace by
  `TileHeader` / `ChunkCodec.ReadHeader` as above.
- A branch that edited `CoverFormat/HoleFormat/TreeFormat/ChunkCodec.Encode|Decode` or
  `TerrainBuild.IsValidTerr/ReadHeaderTile` conflicts: take main's version and re-apply only the
  non-header part of the change.
- A branch that changes a format (new version, flags) bumps the golden hash in the same commit and
  says why in it; a golden change without a format bump is a bug.
- Open PRs touching these files when this landed: none besides #232 (base of this PR).

## How to check

`tools/test.sh unit` (or `dotnet test tests/UnitSportSwitzerland.Tests`): `TileHeaderGoldenTests`
and the round trips in `TerrainFormatTests`.
