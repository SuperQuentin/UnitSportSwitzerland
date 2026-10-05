# Developer setup

From a fresh clone to a running game, and optionally a small piece of real terrain to work on.
Written against Windows, which is what the project is developed on; the same tools exist for
macOS and Linux.

## 1. Install the toolchain

| Tool | Version | Needed for |
|---|---|---|
| **Godot, .NET edition** | **4.7.1** exactly | the editor and the game. It must be the *.NET / mono* build, and match `Godot.NET.Sdk/4.7.1` in `UnitSportSwitzerland.csproj` |
| **.NET SDK 8** | 8.0.x | the game assembly (`net8.0`) |
| **.NET SDK 9** | 9.0.x | `tools/TerrainPreprocessor` (`net9.0`). The 9 SDK builds the net8 projects too, so strictly it is the only SDK you need |
| Python | 3.10+ | *optional*: `tools/swiss_data.py`, the data downloader (standard library only) |
| GDAL (Python bindings) | any recent | *optional*: `tools/export_buildings.py` only. Cycle routes no longer need it (#537) |

A .NET **runtime** is not enough: `dotnet --list-sdks` must list both an 8.x and a 9.x SDK.

On Windows, from an **elevated** PowerShell:

```powershell
choco install godot-mono --version 4.7.1 -y
winget install Microsoft.DotNet.SDK.8
winget install Microsoft.DotNet.SDK.9
winget install Python.Python.3.12 --scope user     # optional
```

Chocolatey puts Godot where the commands in `CLAUDE.md` expect it:

```
C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe
```

Use the `_console.exe` when running from a terminal: it prints the game's log. Without
Chocolatey, download the **.NET** build of 4.7.1 from the Godot archive
(`godotengine.org/download/archive`) and substitute its path wherever this guide says `godot`.

## 2. Build and open

```bash
dotnet build UnitSportSwitzerland.sln
```

Then either open the project in the Godot editor (Import → `project.godot`), or do the first
import headless:

```bash
godot --headless --path . --import
```

The first import creates `.godot/` and can take a minute. Run the game with **F5** in the editor, or:

```bash
godot --path .
```

## 3. Check that it works

With no terrain yet, the game boots into generated terrain and says so. That is the normal
state of a fresh clone, not a failure; real terrain takes over tile by tile as soon as there is
some, and the generated ground round it stays and bends to meet it:

```bash
godot --path . -- --shot 0,700,400,-15,0,5,test_output/smoke.png
```

Expected output, and a screenshot of a valley village beside a river in `test_output/`:

```
WARNING: [world] no terrain data found, showing generated terrain. Generate the real one with ...
[terrain] generated fill on: 0 real tiles, domain TileRect { MinE = 2543, MinN = 1073, MaxE = 2623, MaxN = 1153 }
[shot] wrote test_output/smoke.png (1152x648) at (0, 700, 400)
[shot] fps=60 prims=8527444 draws=67 mem=158MB
```

Put screenshots and probe output in `test_output/`; it is gitignored and hidden from Godot.

## 4. Get a world to walk around

Neither the source geodata (~155 GB) nor the generated tiles (~5 GB) are in git. There are two
ways to get terrain.

### Option A: stream it from a server

If someone on the team runs a dedicated server, connect to it. Every tile you walk near is
downloaded and cached in `user://chunk_cache/`, and stays available offline afterwards.

```bash
godot --path . -- --connect <host>[:7777] --name <you>
```

### Option B: build a small region yourself

This builds 3 × 3 km around Riddes, the default spawn point. It downloads about 5 GB, nearly
all of it the nationwide swissTLM3D file, which you only fetch once.

```bash
# 1. terrain heights: ~17 tiles, ~330 MB
python tools/swiss_data.py swissalti3d --bbox 2582000 1112000 2585000 1115000

# 2. build .terr tiles + manifest.json
dotnet run --project tools/TerrainPreprocessor -c Release -- \
  --in ressources/data/swiss_chunks --out terrain_chunks --verify
```

You now have bare terrain. For roads, rail, water, land cover and trees, add swissTLM3D:

```bash
# 3. swissTLM3D (4.8 GB zip), then unzip it in place
python tools/swiss_data.py swisstlm3d
#    -> unzip ressources/data/tlm3d/swisstlm3d_*.gpkg.zip into ressources/data/tlm3d/

# 4. roads + land cover, then the junction rewrite
dotnet run --project tools/TerrainPreprocessor -c Release -- \
  --out terrain_chunks --features-only --cover \
  --tlm ressources/data/tlm3d/<the .gpkg you unzipped>
dotnet run --project tools/RoadGen -c Release -- --rewrite --chunks terrain_chunks
```

Always run `RoadGen --rewrite` last. Any preprocessor run that touches roads strips the
junction polygons, and the rewrite refuses to run twice on the same files.

For towns and summits in the **Tab** search, fetch the building register (59 MB for Valais)
and add two flags to step 4 before the rewrite:

```bash
python tools/swiss_data.py gwr --canton vs
#    -> unzip ressources/data/gwr/gwr_vs.zip so that ressources/data/gwr/data.sqlite exists
#    step 4, plus:  --places --gwr ressources/data/gwr/data.sqlite
```

`--places` refuses to run without `--gwr`. Buildings also need GDAL and one more export step;
see *Add roads, land cover and buildings* in the README.

Then run the game again. You spawn over Riddes.

## 5. Editor and tooling

- **C# IDE**: VS Code with the C# Dev Kit, or Rider, opened on `UnitSportSwitzerland.sln`.
  RoadGen is a separate project (`tools/RoadGen/RoadGen.csproj`) and is not in the solution.
- **godot-ai MCP**: the `addons/godot_ai` plugin is enabled in `project.godot`, so an AI
  agent can drive the running editor and game. When it is not connected, the `--shot`,
  `--probe` and `--ride` flags do the same verification from the command line.
- Read **`CLAUDE.md`** before changing file formats, shaders, or anything that has to line up
  with the terrain grid. Its *Gotchas* section is the project's institutional memory.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `NETSDK1045: The current .NET SDK does not support targeting .NET 9.0` | only the 8 SDK is installed; the preprocessor needs the 9 SDK |
| `No SDKs were found` although `dotnet` runs | only a .NET runtime is installed; install an SDK |
| `Python was not found; run without arguments to install from the Microsoft Store` | the Windows Store alias is shadowing Python: open a new terminal after installing, use `py`, or turn off *App execution aliases* for python in Windows settings |
| C# scripts show as missing in the editor, or the editor offers no *Build* button | this is the standard Godot, not the .NET build |
| Empty world, sky only | expected with no `terrain_chunks/`; see step 4 |
| Python HTTPS fails with `ASN1: NOT_ENOUGH_DATA` | a conda GDAL install replaced openssl. Keep the downloader on a plain Python, and GDAL in a separate conda env |
| Close-up `--shot` comes back as an aerial view | coordinates are world-space relative to the manifest origin, which moves whenever the tile set grows |
