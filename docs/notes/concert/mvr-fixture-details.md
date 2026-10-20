# MVR fixture details: focus, invert, breaks, screens, MVR 1.6 export (#959)

Builds on `mvr.md`. Code: `src/Concert/Mvr/MvrFixtures.cs` (helpers), `Mvr.cs` (`AddFixture`, `Write`),
`Gdtf.cs` (breaks), `RigData.cs` (fields, `Check`); tests `tests/.../MvrFixtureTests.cs`.

- **Our beam model.** A head's beam at pan and tilt centre runs along `Fixture.Up` (the yoke axis);
  `Home` is only the pan reference (tilt leans toward it). So a focus is not a new `Home`: it is a pan
  from home and a tilt from the yoke axis (`FixtureRow.FocusPan/FocusTilt`, degrees, and
  `FocusDistance`, m), angles and a length so `StagePlan.FromStage` needs no change. `PatchInto` clamps
  them to the head's ranges into `Fixture.HomePan/HomeTilt`; `ConcertRig.Direction` adds them, so the
  rig points at the focus at DMX centre and the desk's moves swing around it. The matrix (Up, Home)
  stays as the file gives it.
- **Focus import.** `<Focus>uuid</Focus>` (any UUID spelling, the spec's example spaces it) looks up a
  `FocusPoint` anywhere in the layer tree, parents composed (`Index`). A head out of range is noted
  ("cannot reach": leaned as far as it goes); a fixed light whose point is more than 2 degrees off its
  beam is noted and keeps its matrix. Export writes a `FocusPoint` per fixture with a focus (10 m out
  when only the clamped angles survived, as on a client that did not import the file).
- **DMXInvertPan/Tilt** (`ip`/`it`): `LightDesk.Aim` writes `1 - v`, `ConcertRig` reads `1 - v`, so the
  desk looks the same and an external console's DMX means what it means on the real head. Exported back.
- **Breaks.** MVR's `Address break="n"` is GDTF's `DMXBreak` n+1 (MVR counts from 0, GDTF from 1).
  `Gdtf.FromXml` reads every break (`Channel.Break`, `Type.Breaks`); `ToFixtureType(g, out why, brk)`
  makes one of our types per break. A fixture whose file patches break 2+ gets `FixtureRow.Breaks`
  (`{break, type, universe, address}`, at most 8): `PatchInto` adds each as its own fixture at its own
  address, same place, its group by its kind (a ring of cells is a pixel bar). Not patched in the file:
  only break 1 is lit, noted. GDTF geometry references are expanded: a template's channels repeat at
  each `GeometryReference`'s `Break` offset (`DMXBreak="Overwrite"` takes the reference's last Break).
- **VideoScreen** (any depth) becomes our screen fixture: its GDTF's type when that is a screen, else
  our media-server layer at its address (noted); faces its -Y (the audience in a plot whose Y runs
  upstage); size our 8 x 4.5 m wall scaled by its matrix's X and Z (its geometry is not measured).
- **CustomCommands**: `...Zoom...,f <deg>` on a head with no DMX zoom sets that fixture's beam (a type
  variant "(zoom 44)"); written back as `Zoom,f 44` when the original GDTF is kept. Others ignored.
- **Group**: an AUXData `Position` named like a `RigGroup` first, then the layer's name, then `GroupOf`.
- **Export (MVR 1.6)**: `verMajor/verMinor` 1.6, `provider`/`providerVersion`; per fixture `FixtureID`
  and `FixtureIDNumeric` (an imported one's own when unique, else the next free), `Classing` and
  `Position` (one `Class` and one `Position` per rig group in AUXData), the imported uuid (`id`) or a
  derived one, never nil or repeated; no NaN/Infinity (`F` writes 0); flat archive.
- **Original GDTFs.** `TypeRow.GdtfSpec/GdtfMode` (`gs`/`gm`) travel with the rig; the bytes do not.
  `ConcertMvrTool` caches them per rig source on the importing machine (`MvrFile.Gdtfs`), and `Export`
  writes an imported rig (`ConcertRig.Imported`) with `MvrFile.Write(..., originals)`: each GDTF byte
  for byte under its own spec and mode, a fixture's breaks as addresses on it. Another client, or after
  a restart, writes ours: each break then a fixture of its own. An imported rig exports as imported
  (not the stage's generated screens and effects `UseRig` kept).
- **Check limits** (a client sends them): `gs`/`gm` 256 chars; uuid a non-nil GUID; fixture ID >= 0;
  focus pan within 360, tilt 180, distance 0..1000 m, all finite; home and up finite; screen size
  0..200 m; breaks <= 8, break 2..64, a known type, inside a universe; fixtures plus breaks <= 4000.
- **Real files** (Oct 2026): `scene_objects.mvr` 72 pendants aimed at their 72 points (all straight
  down: no visible change), zoom 44; all four samples re-export with their GDTFs identical, the same
  uuids and focus, and open in `pymvr`/`pygdtf`. No sample has DMXInvert, a second break or a
  VideoScreen: those are covered by `MvrFixtureTests` only.
