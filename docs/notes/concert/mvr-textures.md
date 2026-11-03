# MVR imports: per-face colours and textures (#973)

- **What.** Imported 3D (MVR scene objects, #958; GDTF bodies, #961) keeps each face's own look in
  place of one colour per file: a 3DS face's material (`0xAFFF`: `0xA000` name, `0xA020` diffuse,
  `0xA200`/`0xA300` texture map file, faces named by `0x4130`, coordinates `0x4140`, v flipped) and a
  glTF primitive's `baseColorFactor` and `baseColorTexture` (`TEXCOORD_0` float or normalised u8/u16;
  image as a data URI, a `bufferView`, or a file in the archive). A texture map replaces a 3DS diffuse
  colour (3DS draws one at full strength); a glTF texture multiplies its factor (white when none).
  Faces without coordinates are not textured. Code: `MvrGeometry.cs` (`Looks` splits a vertex two
  looks share), `MvrImage.cs`, `RigData.Objects.cs`, `ConcertRig.Objects.cs`.
- **`MvrMesh`** keeps its constructor; optional `Colors` (per vertex, sRGB), `Uvs` (Godot's, v down),
  `TriangleImages` (into `Images`, -1 none), `Images`; `ColorAt(v)`, `ImageOf(t)`. `Color` is still
  the colour of most triangles (a textured face counted at its texture's mean).
- **Images** (`MvrImages`, pure .NET): PNGs of every colour type and depth (not interlaced) decoded
  row by row straight into a box filter, so a 4k image reads in little memory; at most 64 px a side,
  RGB, re-encoded as a PNG with its mean colour (`MvrImage`). JPEG, BMP, TGA, WebP and interlaced
  PNG go to `MvrImages.Fallback`, which `ConcertRig` sets to Godot's loaders (a `ModuleInitializer`,
  game only): unit tests cover PNG only.
- **Decimation** (`MvrFile.Decimate`) keeps looks: a cell's vertices merge per (colour, image), so
  seams stay seams; texture coordinates merging into one vertex are averaged (a textured mesh cut
  hard loses some mapping, which the low-poly look forgives).
- **On the wire.** `RigData.Textures` (`tx`): the PNGs, shared by objects and bodies, deduplicated by
  bytes. A mesh row may add `vc` (RGB8 a vertex), `uv` (two u16 a vertex over `ul`/`us`) and `ts`
  (runs: texture or -1, triangle count; triangles sorted untextured first); all null when unused, so
  Capture's rows are as before. Vertices weld only with the same colour, texture and coordinate.
  Accessors: `VertexColorsOf`, `UvsOf`, `RunsOf` (`Positions`/`Indices` unchanged). `Bytes` counts
  colours and coordinates against the scene's 1.2 MB and the bodies' 300 kB.
- **Caps** (`Check`, server and every client): 64 textures, each a PNG header of at most 64 x 64 and
  16 kB, 256 kB in all; colours one a vertex; runs covering the triangles, of existing textures;
  coordinates one a vertex, their box within 4096. The server never decodes a texture; clients decode
  with the same pure reader.
- **Budget at import** (`MvrFile.Fit`): distinct images most-drawn first while `AddTexture` accepts
  them; one left off draws its faces at its mean colour (`Quantise` multiplies). Notes: "N texture(s)
  on the objects ... (kB)", "N texture(s) left off past the budget ...: their faces keep their
  colours". Textures of meshes dropped later are compacted away (`CompactTextures`). A body's
  textures share the budget (`AddBody`); past it the body keeps its colours.
- **Drawn.** One surface per texture (baked one-offs and each MultiMesh mesh alike), the prop material
  with `albedo_texture` set (`shaders/body/prop.gdshaderinc`: `source_color, filter_nearest,
  repeat_enable, hint_default_white`, times the vertex colour; unset it is white, so other props are
  unchanged). One material per texture, made once; vertex alpha stays 0 (no glow).
- **Real files** (Oct 2026, `MVR_SAMPLES=<dir> dotnet test --filter MvrTextureTests.Real_files`):
  `capture_demo_show.mvr`: its 891 3DS have no materials at all, so it looks as before (grey).
  `scene_objects.mvr`: 18 glbs carry textures (`Texture-1.png`, `Yellow Surface.png`, a carpet; 256 px
  and 400 px, read to 64 px, 3-7 kB), but they are the room (Vectorworks `Site-` classes, the
  "Theatre FloorPlan" layer): not drawn. With the classes stripped (a local copy) the risers draw
  textured: 2 textures, 11 kB, 16 of 50 meshes. `basic_gdtf.mvr`: the MegaPointe's 3DS materials have
  no diffuse; one of its three body parts has two colours. BlenderDMX's PAR: factors only.
- Checks: `MvrTextureTests` (PNG filters, palette, 16-bit, shrink; 3DS two materials and a map; glTF
  factor plus embedded and archive textures; wire round trip and what `Check` refuses; the budget;
  decimation; a body's texture); `tools/concertnetcheck.sh` (B gets a mesh with a red face and a
  texture and draws it: `ConcertRig.TexturesDrawn`).
- Not done: texture alpha (cut-outs), 3DS map tiling/offset chunks (`0xA354`...), `KHR_texture_transform`,
  glTF `COLOR_0`, a second UV set, mipmaps.
