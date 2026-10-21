# MVR and GDTF: a concert rig out to lighting tools and back (#941)

- **Formats.** MVR (My Virtual Rig, DIN SPEC 15801): a zip, `GeneralSceneDescription.xml` (layers,
  fixtures with a matrix, a GDTF, a mode, a DMX address) plus the GDTFs and 3D files. GDTF (DIN SPEC
  15800): a zip, `description.xml` (attributes, geometries, DMX modes). Code: `src/Concert/Mvr/`, pure
  .NET (zip and XML), unit-tested in `tests/.../MvrTests.cs`.
- **Export** (`/concert export`, the desk's *Export MVR*): the nearest rig to
  `user://concert/mvr/<stadium> - <artist>.mvr`: a layer per `RigGroup`, every fixture's matrix (mm,
  Z up, origin the centre spot), its absolute address (universe x 512 + address, 1-based), a GDTF per
  type (`Gdtf.Write`: standard attribute names, pan/tilt/dimmer 16-bit folded as `Offset="1,2"`, base >
  yoke > head > beam or one beam per cell), and the stage and PA as `stage.glb` (Godot `GltfDocument`,
  Y up metres, as glTF is). Our exact kind and ranges ride in each GDTF's `Description` (five effects
  share arm/fire); the frame, the exact home and screens in the scene's `UserData` (provider
  UnitSport). Checked with BlenderDMX's `pymvr`/`pygdtf` (Oct 2026): every layer, fixture, address
  and GDTF read, footprints right.
- **Frames.** Ours to MVR: x stays, y = -z, z = y (`MvrFile.ToMvr`). A fixture's GDTF Z is minus our
  yoke axis (`Fixture.Up`: the beam leaves along -Z at pan and tilt zero), its Y our home levelled
  across the yoke. Pan is written reversed (`PhysicalFrom = +range/2`): ours turns toward the right of
  home, GDTF's Z rotation the other way.
- **Import** (`/concert import <file> [stage|stadium]`, `off`; the desk's MVR row lists
  `user://concert/mvr/*.mvr`, *Folder* opens it): reads the scene (nested trusses and groups composed),
  each GDTF mode mapped by `Gdtf.ToFixtureType`: channels to our `Attr` by name (`Parse`), the rest
  `Attr.Other` so offsets hold; a profile stripped to numbered placeholders (`Dimmer1`..`DimmerN`, as
  Capture's exported demo shows are) is hung as our own spot, wash or beam by its name, a static RGBW
  wash otherwise (`Gdtf.Placeholder`), noted; a kind from what it can do (pan/tilt + RGB = wash, narrow or prism =
  beam, else spot; R/G/B on several cells = pixel bar, dimmers on cells = blinder, strobe rate = strobe,
  fog = hazer, a static RGB par = a wash that never moves). Groups from our layer names, else
  `MvrFile.GroupOf` (kind, yoke up or down). Order by `FixtureIDNumeric`. Ours lands in the stadium
  frame exactly; a fixture at address 0 or none (a plot drawn before its patch) is patched after the
  last address in use, never across a universe's end, noted; anyone else's in the stage's (`StagePlan.FromStage`: origin the deck's front centre on
  the pitch, MVR Y upstage). Devices it cannot show are skipped and listed in the log.
- **On a hosted set only.** The client packs the rig (`RigData`, gzipped JSON, ~10 kB for 134
  fixtures), the server checks it (`RigData.Check`: 128 types, 4000 fixtures, 64 universes, addresses
  inside their universe, within 400 m) and only from the set's operator, then `RigSet` to every client
  and joiner (protocol 42); `StagePlan.UseRig` patches it in place of the generated rig and keeps the
  stage's screens, haze and effects when it has none. Hosting or releasing drops it.
- **Fixtures not ours.** Cells may start anywhere (`FixtureType.CellOffsets`: a master dimmer before a
  bar's cells, which the desk opens); a head without a dimmer lights by its colours and goes dark by
  them (`LightDesk.Dark`).
- Checks: `MvrTests` (every type through GDTF, a mixed rig through MVR, someone else's wash, bar and
  scene); `--concertcheck` (a generated rig out and back the same patch; a lights-only rig on another
  stage keeps its screens and effects); `tools/concertnetcheck.sh` (A imports, B gets the 134 fixtures,
  then the set's own rig back).
- **Real files** (Oct 2026, #941): python-mvr's test files (MIT, `open-stage/python-mvr/tests`), read
  with `MvrFile.Read`: `capture_demo_show.mvr` (Capture, 76 fixtures, 5 stripped profiles: Clay Paky
  Alpha Spot and A.leda, Robe MMX Spot and WashBeam, ADB ALC4: a V of heads before the screen at
  Geneva), `basic_fixture.mvr` (a BlenderDMX RGBW par, unpatched), `scene_objects.mvr` (72 pendants,
  unpatched), and the spec's `basic_gdtf.mvr` (4 Robe MegaPointes, 39 ch). Not committed (sizes);
  `MvrTests` copies their two quirks. In game: copy one to `user://concert/mvr/`, `/concert host 5`,
  `/concert import capture_demo_show.mvr stage`.
- Someone else's trusses, scenery, screens and focus points (3DS and glTF) are drawn: `mvr-scene-objects`
  (#958). Not done here: multi-break fixtures keep their first break (#959), no live MVR-xchange (#960),
  fixtures drawn as boxes, not their GDTF bodies (#961).
