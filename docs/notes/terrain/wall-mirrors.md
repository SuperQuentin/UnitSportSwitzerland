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
- It sees the world and figures, the VR player's own body included (`XrSession.SpectatorOnlyLayer`),
  so in VR it is where you see yourself and your real hands (`avatar/item-arm-poses`); not the
  held-item viewmodel nor the door portals' quads.
- Mirrors are in the `WallMirror.Group` group. Check: `--portaldemo,test_output/pd.png` writes
  `pd_mirror.png` (house B has a basin).
