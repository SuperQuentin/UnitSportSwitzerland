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
  `lanes`, `freeze`, `no<layer>` (`noground`, `noroads`, `nobuildings`, `notrees`, `nowater`, `nohorizon`,
  `noreal`, `nogenerated`, `nofill`), and one view: `wireframe`, `clay`, `colours`, `overdraw`.
  Example: `--origin 2583250,1113250 --shot-queue q.txt --debugview open,tiles,labels,origin`.
- **Checks**: `--debugcheck --systems ui` (quick, `Core/DebugMenuCheck`): F9, mouse clicks pushed into
  the viewport, Space, Esc (not the pause menu), `/debug`, clay through a `/style` restyle and back, the lane plan drawn once with its legend and cleared.
  `tools/debugcheck.sh` (net): a server with `--admin-password`; refused, `/login`, offered,
  `/admin remove`, every tool off. The view picker is set through `ItemSelected`, not its popup.
- **Traffic** (#971, `world/traffic-generators`): `paths` draws every car's way ahead on the lanes it drives (a generated
  car's to its destination in that destination's colour), `endpoints` the traffic endpoints. "Traffic tool" hands an admin
  the held tool that places, edits and removes generators and destinations (`items/traffic-tool`); "Remove all endpoints";
  they go to the server and everyone's traffic. Example: `--debugview paths,endpoints`. `lights` draws the paths through
  lit junctions that may go now (within 400 m): solid green on green with absolute priority, dashed green on green where they
  must yield (`SignalPlan.YieldsAt`: a flasher on a turn, oncoming cars for a left from a shared lane, a crossing or bike lane
  green across it; straight on keeps its priority), yellow on amber; none on red. The riders' paths (`JPTH` of kind Bike, thin)
  and the crossings (wide, kerb to kerb between the arm's pedestrian heads, on the zebra's yellow paint) by their own groups
  (`SignalSite.Rides`, `Walks`). Separated bike paths have no riders' paths yet (the core planner lays them from painted lanes).
  `movers` (#979) shows, at each phase change of the lights within 400 m, the tightest pair of each green's start and of each
  green's end on the plan's clock, as trails, each shown with its line only while it moves: the ending group's last car / bike / walker (red, crossing its stop line
  as its amber ends, at its clearing speed, until it has left its line's end, its trail held ahead of it), the starting group's first (dark green,
  from its line at green at its entering speed to its line's end, its trail behind it back to its line; a crossing's walkers from both kerbs at once), their lines faint
  and the conflict area ringed in white: the green one reaches the ring just as the red one has left it (`SignalSite.Pairs`, from `CPAR`).
  `trails` (#979) draws only the trails, one per movement the timing took (`SignalSite.Lines`) through each of its greens: grown from its line at
  green at its entering speed, closed behind its last mover from its amber's end at its clearing speed, in its group's light
  now (green, yellow on amber, red), so a trail whose green ends before it reaches its exit turns yellow then red as it is; no
  movers, lines or rings (a red-amber shows nothing), see-through over the cars, dashed (on yellow and red too: its last ones still give way) where it must yield as the
  green paths (`SignalPlan.YieldsAt`, its turn read off the line's first and last stretches), and where the plan lets it
  cross an exit's crossing with its walkers (a permissive conflict: riders turning right) for as long as walkers are on it
  (their green to the last across, through their yellow and red); a protected turn, kept apart by the times, never.
  Trails and movers redraw every frame, the other tools five times a second. The debug lines' material is alpha-blended.
- **Lane plan** (#1181, `lanes`, `Terrain/LanePlanDebug`): the junction planner's cross-sections within 650 m seen from
  above, over everything (no depth test): every street, `TRNS` transition piece and junction core as its strips coloured by
  kind (legend top right: car lane arriving blue / leaving cyan / neither grey, turn pocket violet (an arriving lane with no
  through move, or one opening along its transition), bike lane yellow, path red, verge and buffer green, sidewalk, flush
  paving, kerb black, hatch pink, island orange, tram track magenta, core dark grey, parking bay light blue). Arriving or
  leaving by the junction its point belongs to: the street's mouth nearer it, else the nearest core ahead or behind (400 m).
  A transition's strips are the planner's; a plain segment's from `SegmentSections` and its attributes (lane counts, else
  the class's guess: one way all along, two ways half each, one shared lane under 5 m; painted bike lanes; a pocket's
  widening; the side's profile). White arrows: an arriving lane's moves 1.5 m back from its mouth (a `LANE` record's own
  across its stop line where no street mouth is drawn there). Labels 14 m out on each transition's mouth and each `LANE`
  approach: the record's lanes as the test region's log names them (`LANE L | bL | T | R | bTR`), under them the strips
  left to right as the arriving driver sees them (`walk | path ‖ out | hatch | L | bL | T | TR ‖ path | walk`; `‖` the
  carriageway's edges, `in`/`out`/`both` a lane without moves). Drawn once on the switch, when the camera has moved 300 m,
  and on an origin shift (from the tiles in hand, node at the identity); `[lanes]` logs each build. Example:
  `--chunks test_output/signal-region --generated off --origin 2910500,1322500 --at 2910500,1322500 --debugview lanes`
  with a top-down shot `0,g95,0,-90,0,6,out.png`.
- **Z-fighting** overlay (`zfight`) and **Flicker shot** button (`--debugview flicker`): `general/zfight` (#788).
