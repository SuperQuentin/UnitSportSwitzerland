# GDTF fixture bodies: each imported fixture drawn as its own model (#961)

- **What.** An imported fixture whose GDTF has a geometry tree with models is drawn as that fixture
  (a Robe MegaPointe's base, yoke and head) in place of the rig's generic black box; pan turns its yoke
  and head, tilt its head, and its beam and flare leave the lens. Our own GDTFs (`Manufacturer`
  UnitSport) get none: ours keep the rig's look. Code: `src/Concert/Mvr/GdtfBody.cs` (pure),
  `RigData.Bodies.cs`, `ConcertRig.Bodies.cs`.
- **Reading** (`GdtfBody.Of`, from `Gdtf.Read`, which has the zip): the mode's `Geometry` (else every
  top-level geometry), walked down with each node's `Position` (four rows, translation in the fourth
  column, metres; `GdtfBody.Matrix`). The pan axis is the geometry the Pan channel names, the tilt
  axis the Tilt channel's (a name not found: the first `Axis`, and the first inside it); no pan or
  tilt channel: one rigid body. Parts: base = outside the pan axis, yoke = the pan axis' subtree
  outside the tilt axis, head = the tilt axis' subtree; pivots are the axes' origins; the beam point is
  the first `Beam` in the head (else anywhere). `GeometryReference` walks its top-level geometry at
  the reference's place (its `Model` swaps the root's). At most 4096 nodes, 400k triangles.
- **Models.** `File` (no extension) is `models/gltf/<f>.glb`, `.gltf`, then `models/3ds/<f>.3ds`, read
  by `MvrGeometry` (#958); its box is scaled per axis to `Length` (X), `Width` (Y), `Height` (Z) about
  the file's origin, as GDTF says (a MegaPointe's 3DS boxes are its dimensions in mm exactly). No file:
  the `PrimitiveType`: Cube, Head, Scanner, Pigtail a centred box; Base a box hanging below its point
  (Z -H..0, as BlenderDMX's primitive models); Cylinder centred, 10 sides; Conventional (a par) a
  cylinder hanging; Sphere 8 x 6; Yoke a bar and two arms, centred; Undefined nothing. A model under
  3 mm thick (a lens disc, an emitter plane) is not drawn: the flare is. Primitives are black.
- **Frame.** Parts are in the fixture's GDTF frame at rest (Z = minus our yoke axis, Y = home):
  `MvrFile.Axes(up, home)` at the fixture's place is the base's transform. `GdtfBody.Pose`: the yoke
  turns by -pan about Z through its pivot, the head by tilt about X through its pivot, so the head's -Z
  is exactly `ConcertRig.Direction` (checked in `GdtfBodyTests`).
- **On the wire.** `RigData.Bodies` (`bm`): mesh rows as the scene objects' (u16 over the box), each
  part decimated to 600 triangles (`MvrFile.Decimate`), equal parts shared across types; a type row's
  `body`: base/yoke/head indices (-1: none), yoke and head pivots, beam point. `Check` (server and every
  client): 384 meshes, 1..600 triangles each, 300 kB of body meshes, every index in its mesh, boxes and
  pivots within 5 m. Over the budget at import: that type keeps the generic box.
- **Drawn** (`ConcertRig.BuildBodies`): one `MultiMesh` per body mesh (prop material, vertex colour),
  the generic box's instance zero-scaled; bases placed once, a head's yoke and head written in place
  each frame in the head loop (two transforms a head, as the box was one), no allocations.
- **Real files** (Oct 2026, not committed; `MVR_SAMPLES=<dir> GDTF_SAMPLES=<dir> dotnet test --filter
  GdtfBodyTests.Real_files` prints them): `basic_gdtf.mvr` Robe MegaPointe, three 3DS parts (600
  triangles each, its file's grey cccccc), pivots -0.095 and -0.375 m, beam -0.632 m: four heads on
  the truss before the deck, panning and tilting with the show. `scene_objects.mvr`: the pendants'
  3DS body (588 triangles), rigid. `capture_demo_show.mvr`: stripped profiles (a Base of no model), no
  body, generic boxes. BlenderDMX profiles: BeskydBeam and NeblinaWash (Base1_1, Yoke, Head
  primitives), RysyParLED (glTF body and bracket, rigid: its Yoke axis has no pan), TyrolPAR
  (Conventional1_1), AlpineTubeRGB (a cylinder on its Beam, turned).
- Checks: `GdtfBodyTests`; `tools/concertnetcheck.sh` (B gets a type's body with A's rig and draws it);
  the model viewer's *Concert / GDTF bodies* (primitives, a moving head panned and tilted).
- Not done: textures and per-face colours (one colour per part), axes turned in their own frame
  (pan is about the fixture's Z, tilt about its X, as GDTF draws them), `_low`/`_high` model levels,
  several beams per head (the first leads), a GDTF's `Display`, `Laser` or media geometries' own content.
