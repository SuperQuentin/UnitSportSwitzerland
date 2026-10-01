# Third-party licenses and attribution

UnitSportSwitzerland's own code has **no license file yet** (see the *Licenses* section of
[README.md](README.md)). Everything below is third-party and keeps its own terms.

The repository ships no image, audio, font or 3D-model assets: the world, avatars, vehicles and
sounds are generated in code and shaders. What does ship is software, two small derived data
files, and spec facts for one vehicle. Geodata is downloaded by the user and is not redistributed.

## Software

| Component | Used for | License | Source |
| --- | --- | --- | --- |
| Godot Engine 4.7 (.NET build) | engine, editor, `Godot.NET.Sdk` | MIT, © Godot Engine contributors, Juan Linietsky, Ariel Manzur | <https://godotengine.org/license/> |
| .NET 8 / 9 runtime and SDK | game and tools | MIT, © .NET Foundation and contributors | <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |
| Godot AI (`addons/godot_ai`) | editor MCP plugin | MIT, © 2025 Godot AI contributors | full text in [`addons/godot_ai/LICENSE`](addons/godot_ai/LICENSE) (same file as [`godot-ai-LICENSE.txt`](godot-ai-LICENSE.txt)) |
| Spectre.Console 0.49.1 | `tools/MapSetup` console UI | MIT, © Patrik Svensson, Phil Scott, Nils Andresen | <https://github.com/spectreconsole/spectre.console/blob/main/LICENSE.md> |
| Microsoft.Data.Sqlite 8.0.10 | GWR / SQLite access in `tools/*` | MIT, © .NET Foundation and contributors | <https://github.com/dotnet/efcore/blob/main/LICENSE.txt> |
| GDAL (Python bindings) | optional, `tools/export_*.py` only; not bundled | MIT/X, © GDAL/OGR contributors | <https://gdal.org/en/stable/license.html> |

Godot's own bundled third-party components (FreeType, zlib, ENet, etc.) are listed in the engine's
license screen (Editor → Help → About Godot → Third-party Licenses) and in the Godot export
templates' `COPYRIGHT.txt`.

### MIT License text

Applies to each MIT component above with the copyright holder named in its row.

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Data

| Dataset | Provider | Terms | Attribution to show |
| --- | --- | --- | --- |
| swissALTI3D, swissALTIRegio, swissTLM3D, swissBUILDINGS3D 3.0 | Federal Office of Topography swisstopo | Open Government Data, free use; source must be cited. <https://www.swisstopo.admin.ch/en/terms-of-use-free-geodata-and-geoservices> | "Source: swisstopo" |
| GWR / RegBL (building register) | Federal Statistical Office (FSO / BFS) | Open Government Data, source must be cited. <https://www.bfs.admin.ch/bfs/en/home/registers/federal-register-buildings-dwellings.html> | "Source: Federal Statistical Office (FSO), GWR" |
| Veloland, Mountainbikeland | Federal Roads Office (ASTRA) via geo.admin.ch | Open Government Data, source must be cited | "Source: ASTRA / SwitzerlandMobility" |
| BD TOPO® (optional cross-border France import) | IGN, via the Géoplateforme WFS | Licence Ouverte 2.0 (Etalab). <https://www.etalab.gouv.fr/licence-ouverte-open-licence/> | "Source: IGN, BD TOPO®" |

Derived files committed to the repository, both built from swisstopo data:

- `src/Terrain/swiss_relief.gz`: 500 m heightmap averaged from swissALTIRegio (`tools/swiss_relief.py`).
- `tools/MapSetup/switzerland.bin`: per-km tile index (sizes, survey year, canton, max elevation) baked from swisstopo data.

Both carry the swisstopo attribution above. `terrain_chunks/` output built from these datasets does too
and should not be redistributed without it.

## Vehicle specifications

`docs/data/africa_twin_specs.json` records factual specifications of the Honda XRV650 Africa Twin
(weights, dimensions, power), each with its source URL (Honda press releases, MOTORRAD, others).
It is reference data for tuning the in-game motorbike, not copied text or imagery.
Honda and Africa Twin are trademarks of their owners. This project is unofficial and not affiliated
with or endorsed by Honda.

## Trademarks

Swiss place names, "swisstopo", "Honda" and other names mentioned are used only to describe the
data and vehicles they refer to; they belong to their respective owners.
