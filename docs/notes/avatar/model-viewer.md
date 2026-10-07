# Model viewer (`--models`)

- `<godot> --path . -- --models` (VS Code task `run: model viewer`): every procedural model, one at
  a time on a plain backdrop, in categories. Left/Right (A/D, D-pad) step through a category,
  Up/Down (W/S, D-pad) switch category, drag orbits, wheel zooms; the camera fits the model's box
  corners to the view. A dev tool: keyboard and pad only, no VR way (`src/Avatar/ModelViewer.cs`).
- `--modelsonly <text>` keeps only the models whose category or name holds it (any case), for a quick look or shot.
- `--modelsyaw <deg> [--modelspitch <deg>]` turns the viewer's camera round the model (0 behind it, 180 in front: `150`, `-15` shows a vehicle's nose in a PNG run).
- `--models,<dir>` (e.g. `test_output/models`) writes one PNG per model, prints
  `[models] no viewer entry: <Class>` for builder classes that built nothing during the run, and a
  `[models] RESULT: ok` line (FAILED when a model throws). Run it windowed: headless renders nothing.
  About 1000 models, a few minutes.
- **Rule: every new procedural model is in the viewer** (CLAUDE.md). A model added through an
  existing registry (a car spec, an item, a bird, an enum value) shows by itself; a new builder, or
  a new kind of thing, gets a `[Showcase]` tag or a set in the same change. The screenshot run's
  `no viewer entry` lines are where a missed one shows: a new class there is a missing tag.
- **No list to keep.** The viewer finds `[Showcase("Category", "Name")]` (`src/Core/ShowcaseAttribute.cs`)
  methods by reflection, in any class, private ones too. A tagged method takes no parameters and returns:
  - an `ArrayMesh` / `Mesh`: shown under `HumanMeshBuilder.Material()` (or `FigureMaterial()` with
    `Figure = true`), unless every surface carries its own material;
  - a `Node3D`: shown as is (its parts, materials and `_Ready`);
  - `IEnumerable<(string Name, Func<Node3D> Make)>`: a set, the tag's name a prefix;
  - `IEnumerable<(string Category, string Name, Func<Node3D> Make)>`: a set across categories.
- **Prefer a set read from the area's own registry or enum** over single entries, so new content
  shows by itself: `Rideable.Create` over every `RideKind` (every car, motorbike, truck, boat,
  aircraft), every `ItemId` with a held mesh, `BirdCatalog.All`, every `PropKind` field, every
  `HumanPose`/`Headwear`/emote/fight pose, `BuildGrid.Allowed` pieces, `FurnitureType` at the
  generator's own sizes. Put the method next to the builder (a `*.Showcase.cs` partial for big
  partial classes): it reaches private builders, and nobody edits a central file.
- Untagged parameterless static methods returning a mesh show under **Unlisted** by themselves.
  Give them a tag (or "Parts" for a piece of a bigger model) when you meet one.
- The coverage line comes from `ShowcaseTrace.Mark()`, called where meshes are assembled
  (`MeshScratch.Build`/`BuildInto`, `ChunkNode.Finish`, `InteriorNode.BuildMesh`, the building,
  pier and interior data builders): in the screenshot run it reads the stack. A new assembler that
  bypasses these should call it too. Expected on the list: terrain, road and water tiles (not single
  objects), `ModelCatalog` (imported models), `PhotoVisuals` (a textured card).
- Terrain samples (`src/Terrain/TerrainShowcase.cs`, buildings in `PortalDemo`) build one hand-made
  record each with the game's builders and materials. Without the world's fog and clock, smoke and
  night-only glows look different there than in game.
