# MVR scene objects: trusses, scenery and their 3D (#958)

- **What.** An MVR import draws what the file holds besides fixtures: every `Truss`, `Support`,
  `SceneObject`, `VideoScreen`, `Projector` and `FocusPoint`, wherever it nests (layers,
  `GroupObject`s, other objects' child lists), from its `Geometries`: `Geometry3D fileName=` files and
  `Symbol symdef=` instances of `AUXData/Symdef` (whose child lists hold more of both). Matrices
  compose down the tree as for fixtures (`MvrFile.ParseMatrix`; a `Geometry3D`'s or `Symbol`'s own
  matrix last, so a Capture model's inch scale of 0.0254 applies). Code: `src/Concert/Mvr/MvrScene.cs`.
- **Readers** (`MvrGeometry.Read(fileName, bytes[, resolve], out error)`, pure .NET, xunit-tested;
  GDTF bodies, #961, reuse it): 3DS (chunks 0x4D4D/0x3D3D/0x4000/0x4110/0x4120, faces' 0x4130
  materials' 0xA020 diffuse; vertices are stored placed, the 0x4160 local matrix is not applied; mm)
  and glTF 2.0 (`.glb`, `.gltf` with data-URI or archive buffers; node matrices or TRS; triangles,
  strips, fans; `extensionsRequired` non-empty is refused, as the spec says). Out: `MvrMesh`
  (positions in metres, MVR's frame: Z up; glTF's (x, y, z) is MVR's (x, -z, y), so a glTF is our
  frame as it lies), the colour of most triangles (sRGB; glTF's linear factor converted; none: grey
  0.7, or white for a glTF material without a factor, as glTF says). No extension = 3DS.
- **Into ours.** A mesh's positions through `FromMvr`, the object's transform as
  `FromMvr . B . ToMvr`, origin / 1000 (`MvrFile.ObjectToOurs`). Each distinct file is one mesh
  (by name, then by content hash: Capture writes 891 files, 890 distinct).
- **The venue is not drawn**: a piece on a layer, group or class named site, venue, architect,
  building, floor plan or ceiling (Vectorworks' `Site-` classes, Capture's `Venue` layer), or over
  28 m across (a room's walls, a hall's roof) is skipped and counted in a note: the stadium is the
  venue, and Capture's demo hall otherwise hides the stage. Our own file's `stage.glb` is skipped
  too (we build our stage). Pieces over 300 m from the file's origin are dropped.
- **On the wire** (`RigData.Meshes`, `RigData.Objects`, `RigData.Objects.cs`): a mesh row is a colour
  (RGBA8), a box, u16 positions across it (welded) and u16 indices; an object row its mesh and 12
  floats (basis columns, origin), in the rig's frame. `StagePlan.FromStage` moves them with the
  fixtures (`RigData.MoveObjects`). Caps, in `Check` on the server and every client: 4096 meshes,
  20000 pieces, 20000 triangles a mesh, 150000 in all meshes, 500000 drawn, 1.2 MB of mesh bytes,
  every index inside its mesh, pieces within 450 m. The import keeps to them: each mesh to 20000,
  then all in proportion (vertex clustering, `MvrFile.Decimate`, the finest grid that fits), then the
  heaviest dropped, then pieces past the drawn and count caps; each step noted.
- **Drawn** (`ConcertRig.Objects.cs`, once in `_Ready`): a mesh placed 4 times or more is a
  `MultiMesh`, the rest baked into one mesh (Capture: hundreds of one-off files, one draw call); the
  prop material, vertex colour, no collision (walked under, not on).
- **Real files** (Oct 2026, not committed; `MVR_SAMPLES=<folder> dotnet test --filter Real_files`
  prints them): `capture_demo_show.mvr` 2251 pieces of 874 meshes, 100918 triangles, 942 kB of
  meshes, 559 kB packed, 16 venue pieces skipped (the hall, the Venue layer), 0.2 s; trusses and
  the set's decks and screens before our stage. `scene_objects.mvr` (Vectorworks theatre): its 28
  room pieces are all `Site-` classes, so only the 72 focus points' discs are drawn.
  `basic_gdtf.mvr`: 3 pieces, one simplified from 85712 to 26985 triangles.
- Checks: `MvrSceneTests` (3DS and GLB byte by byte, a gltf's buffers, junk, a Symdef under 200
  trusses in a group, the venue, our own stage, caps and `Check`, `MoveObjects`);
  `tools/concertnetcheck.sh` (B gets A's rig with 3 pieces and draws them).
- Not done: textures (a flat colour per file), per-face colours, collision, `Support`'s and
  `Projector`'s own data (only their bodies), `VideoScreen` sources.
