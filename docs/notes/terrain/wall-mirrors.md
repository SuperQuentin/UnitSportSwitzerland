# Wall mirrors over washbasins (#439)

- `Interiors/WallMirror`: `InteriorNode.Create` hangs a 0.5 × 0.6 m mirror over every `Sink` in the
  layout (bathrooms and WCs), 1.45 m above the floor, on the basin's back wall (its −Z, turned by
  `Turns`). No new furniture type: stored plans hold types as numbers, and a mirror is part of a
  basin.
- It works like the cab mirrors (`player/cockpit`, `Avatar/CabMirrors`): a small viewport (96 px
  high, nearest filter, the PS1 grain) rendering from the viewer's reflection in the glass, with an
  off-axis frustum whose near plane is the glass, shown flipped on an unshaded quad.
- Cost: only the nearest mirror within 5 m that the viewer stands in front of renders, every other
  frame; the others keep their last picture. A mirror's viewport is made the first time it is the
  one. Never on a headless run.
- **Banks** (#1285): mirrors facing one way, in one plane, at one height, side by side (gaps under
  0.6 m, rows up to 4.5 m) are one bank (`WallMirror.AddTo` groups an interior's): its first mirror
  renders one picture as wide as the row through one camera, and every glass shows its slice of it by
  its UVs. So a public toilet's row of basin mirrors is live all along at the cost of one mirror.
  A mirror made on its own (the own-body check's) is a bank of one.
- The model viewer waits a few more frames before a PNG when the model holds a mirror, so its glass is
  rendered in the shot (`Interiors / Public toilets - Gendered`, `--modelsyaw 90 --modelspitch -5
  --modelslook -6.9,1.5,1.35,1.9`: the women's basins, both mirrors showing the stalls behind).
- It sees the world and figures, your own first-person body included (`Avatar.OwnBody.Layer`,
  `avatar/own-body-from-eye`), so it is where you see yourself, in VR with your real hands
  (`avatar/item-arm-poses`); not the held-item viewmodel nor the door portals' quads.
- Mirrors are in the `WallMirror.Group` group. Check: `--portaldemo,test_output/pd.png` writes
  `pd_mirror.png` (house B has a basin).
