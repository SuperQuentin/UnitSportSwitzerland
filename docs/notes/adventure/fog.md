# Fog of war (#1224)

`src/Adventure/Places/FogMask.cs` (pure), `FogTexture.cs`, `fog_map.gdshader`; kept by `PlacesService`.

## Rules

- **Cells**: 250 m on the `CountryData` lattice (west 2 480 km, south 1 070 km, 360 x 230 tiles): 1 440 x 920
  cells, 4 x 4 per 1 km tile, one `ushort` per tile (bit `row * 4 + col`, row 0 the southern), sparse.
- **Reveal** clears the cells whose **centre** is within the radius: 300 m round an earning body each tick,
  a discovery's `max(300 m, radius)`, a viewpoint's reveal radius, a `"reveal"` reward's disc.
- **Stored** in the `fog` section (`FogProgress.Mask`): a varint count, varint gaps between tile indices, the
  masks; deflated, base64. The whole country is under 5 KB. The service keeps a `FogMask` per peer and writes
  the section at most every 5 s and before every save (`AdventureService.Saving`).
- **Never through `SectionSync`**: the service calls `AdventureService.SyncsItself("fog")`. The owner gets
  `FogFull` when its progress loads (`AdventureService.Loaded`: an account, a proof, or the session's once
  told) and `FogDelta` (the changed tiles, at most once a second; a viewpoint's disc at once).
- **Drawn** only on the adventure map (not on the compass or in the world): one L8 `Image` (255 = fog, row 0
  north) built from `PlacesService.MyFog` on first use and patched per delta (`ImageTexture.Update` only then),
  sampled for the view like the relief (`MapView.DrawSampled`) under `fog_map.gdshader`: linear filtering, a
  noisy `smoothstep` edge drifting with `TIME`; per style Cartoon paper clouds with a drawn outline, PS1
  dithered, Realistic a haze.
- The Atlas's **Map** bar: revealed land cells per canton (`AtlasProgress.RevealedLand`: the canton of the
  nearest POI within 2 km, lake cells left out; the file holds no canton lattice) over the file's land cells
  of the canton (`Totals`).

## How to check

`FogMaskTests` (disc, round trip, deltas, a full country under 5 KB, off the lattice); the quick and net
checks of `places`.
