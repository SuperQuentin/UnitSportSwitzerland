# Debug menu (#339)

- **Open it** with **F9** (`PlayerInput.DebugMenu`, listed in F1) or `/debug` in the chat (client-side,
  like `/catalogue`: `ChatManager.DebugRequested`). Offline always; online only for an admin
  (`DebugMenu.Allowed`). Hiding buildings or the ground, or the wireframe view, would be a wallhack in a
  BR match, so losing admin turns every tool off.
- **Files**: `Core/DebugMenu` (the panel, its state, `--debugview`), `Core/DebugOverlay` (lines and
  labels in the world), `Core/DebugView` (view modes). Hooks: `ChunkManager.SetDebugHidden`,
  `FreezeRings`, `ListTiles`, `StrideAt`; `ChunkNode.HideLayers` (`TileLayers` flags);
  `StyleKit.OverrideShader`.
- **The panel is a mouse tool**: its controls have `FocusMode.None`, so the player keeps moving with it
  open and Space never flips the last switch clicked. It shows the mouse and puts the capture back on
  close only if it was captured before. Tools stay on when it closes; Esc or F9 closes it.
- **Overlays**: tile outlines draped on the ground (`TryGetHeight`, 2 m up), inset 4 m so two neighbours
  both show, coloured by stride (`DebugOverlay.StrideColour`: green full resolution -> red coarse, grey
  not built, purple grid only), dashed for generated tiles, a 120 m mast at each NW corner. More rings
  the higher the camera (3..12). Labels (`Label3D`, no depth test) on the 5 x 5 tiles around the camera.
  World origin: an x-ray mast and axes at world 0 (east red, north blue) and the
  `OriginShifter.ThresholdM` circle on the ground.
- **The overlay keeps no world position**: it is rebuilt into one `ArrayMesh` once a second, when the
  camera changes tile and on every origin shift (`IOriginShiftAware`), with its node back at the identity.
- **Terrain layers**: ground, roads, buildings, water are `Visible` on the tile's instances; trees go
  through `VisibleInstanceCount = 0` (`ChunkNode.ApplyTreeDensity`), because `Fill` already owns their
  `Visible`. New tiles take the flags in `ChunkManager.EnsureNode`. Real / generated hide whole
  `ChunkNode`s. The horizon and `NearTrees` are nodes of their own (`ClientWorld.ApplyNearTrees` re-applies
  the flag to a new one). "Generated fill" is the saved setting, not a debug state.
- **View modes**: clay and vertex colours swap the shader of every live world material
  (`StyleKit.OverrideShader`, applied last in `Configure`, so a restyle keeps it and the style's
  parameters survive). The shader has no `vertex()`: PS1 snap, wind and tree billboards are gone, roads
  z-fight the ground. Wireframe needs `RenderingServer.SetDebugGenerateWireframes(true)` before a mesh
  is made, so switching to it rebuilds the tiles (`RebuildVisuals`); figures made before stay
  invisible in it. It is slow: at Riddes, 33 M primitives at 2 fps (the stride-1 ground is ~2 M
  triangles a tile); hide the ground to look at the rest. Godot's "unshaded" / "lighting" draws show nothing new: every world shader is
  `unshaded`. Overdraw is the viewport's.
- **Leaving the world** restores what outlives it: the root viewport's debug draw, the kit's shaders
  and `Engine.TimeScale` (`DebugMenu._ExitTree`).
- **Time scale** is offline only (online the server keeps the clock).
- **`--debugview a,b,...`** turns tools on at boot, for screenshots: `open`, `tiles`, `labels`, `origin`,
  `freeze`, `no<layer>` (`noground`, `noroads`, `nobuildings`, `notrees`, `nowater`, `nohorizon`,
  `noreal`, `nogenerated`, `nofill`), and one view: `wireframe`, `clay`, `colours`, `overdraw`.
  Example: `--origin 2583250,1113250 --shot-queue q.txt --debugview open,tiles,labels,origin`.
