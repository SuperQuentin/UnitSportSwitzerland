# plancues: architectural PDFs into 2D plan cues (#1572, proof of concept)

`tools/plancues/` (Python, PyMuPDF + OpenCV + shapely + numpy, pinned in `requirements.txt`) reads the raw plans collected
for special buildings and writes one **cue** JSON per building. Raw documents (`X:/UnitSportSwitzerland/{Sport,Universities,Landmarks}`),
the venv, renders, traced outlines and every output stay outside the repo (`X:/UnitSportSwitzerland/plancues/`): the sources are
copyrighted. Only synthetic test PDFs (made by the tests) are in the repo. No game code is touched: exports are proposals.

## Commands (`python -I tools/plancues/plancues.py ...`, pages are 1-based)

- `sort <venue|all> [--vlm] [--labels sort_labels.csv] [--only-unsure]` -> `out/<venue>/sort_report.csv` (type, whether the rules
  were unsure, duplicate_of, regions of a sheet, failures recorded and skipped). Types: floor_plan, site_plan, section, elevation,
  seating_plan, access_map, mixed_sheet, photo, text.
- `scale|extract|place|run <venue> <file> <page> [--region K]`: scale (bar, then known object), walls/rooms/doors/outline, fit to
  the map; `run` does all of them and draws the review. Cues: `out/<venue>/cues/*.json`; debug rasters in `out/<venue>/debug_*`.
- `export <venue> house [--exit-snap M]|stadium`: `house-layouts_<cue>.json` (HouseLayout v2, written only if it validates; a Python mirror of
  `HouseLayout.Validate`, plus a `.report.json` of what was left out) or `stadium_row.cs.txt/.diff/.json` (a `StadiumSpec` row and a diff
  against `StadiumCatalog.cs`, which is never edited).
- `review <venue>`: the cue over the swissimage aerial (3 px/m, the WMS of `terrain/stadiums`) and over its source page.
- Tests: `cd tools/plancues && python -m unittest` (synthetic PDFs, no X: needed).

## How it works

- **Scale**: graphic bar = numbers in a row matched to the vertical ticks and rectangle edges beside them (any anchor, least squares, residual
  checked); known object = a football pitch (105 x 68 m: a box of that aspect with a halfway line and a centre circle) or an ice rink; a written
  "1:100" is never used (UniS: 4.53 pt per metre on an A4 sheet). Footprint fit is the last resort and is circular, so it caps a cue at
  low_confidence. One bar serves a whole sheet; a long section with no bar is scaled by the building's length known from the plan (`cross_sheet`).
- **Walls**: the heaviest dark stroke weights that carry a real share of the page's paths (at least 1.5 %, and 20) and every weight within 65 %
  of them; thin filled shapes (white or dark) of that weight are wall solids; short open arcs are door swings and are left out so a doorway
  stays open. Rasterised at 10 px/m; pieces small next to the main structure (furniture, leader lines) are dropped.
- **Rooms**: watershed on the distance transform of the free space (a door is a neck between two basins), grown to the wall middles, grown 12 cm,
  made disjoint, simplified together (`shapely.coverage_simplify`) so shared walls stay shared. Doors are the contacts of two basins; entrances are
  gaps in the outer wall and red arrow symbols at the outline. Labels are words of the PDF text layer inside each room (no OCR needed so far).
- **Place**: buildings: the outline is correlated with the footprint mask over every turn (the north arrow gives the hint) and shift. Stadiums:
  our tiles have no bowl (Tuilière has no solid at all and its `SportsField` cells are the whole sports area), so the grass is matched in the
  aerial, then sharpened on the pitch's own centre circle and halfway line; the north arrow picks the end.
- **Raster pages** (scans): outline only, in pixels, scale from the footprint, so low_confidence at best and rejected on a poor fit.
- **Schematic maps** (a page that says site plan or access map and whose scale bar no known object confirms) are rejected before any geometry.

## Measured (PoC)

| Venue | Result |
|---|---|
| Tuilière ground floor | Bar 0.17644 m/pt, pitch cross-check 0.17639 (ratio 1.0003); pitch 105.03 x 68.02 m; seating front 114.05 x 75.15; roof outer 161.93 x 128.53. Placed on the centre circle: yaw 297.146 (north arrow: 297.15), roof outline median 0.5 m and p90 1.5 m from the aerial's edge. Proposed row E 2537360.5, N 1155029.2, azimuth -27.1 (catalog 2537360.3, 1155029.3, -27.2), depths N 23.9 E 24.0 S 23.9 W 29.4 (catalog 23, 23.5, 25, 29.5). The long section, scaled by the roof length (0.1411 m/pt, which is exactly 1:400), gives a roof 15.0 m over the pitch against the catalog's 22 and 20, and on the end stand 0.80 m treads and 0.55 m risers. |
| UniS ground floor (p2) and UG sheet (p3, region 2) | Bar 0.2207 m/pt. 84 and 118 rooms, 37 doors. IoU with the solid's footprint 0.73 and 0.84 (rms 4.3 and 3.5 m): low_confidence. **No layout passes**: see below. |
| Maladière `plan_01.jpg` | A faint scan of the parking level, no scale: rejected (IoU 0.50, the outline took the ramp and the caption). The PDF's plans are thumbnails in magazine pages (mixed sheets). |
| swissporarena site plan | Rejected as a schematic map before extraction. |

## UniS house layout: why it is a miss

The EGID's solid (1232343, 3455 m2) is the whole complex, but `BuildingOutline` takes the **floor triangles at the lowest height**: a
422 m2 ring, wing B of the Untergeschoss, 28 x 16 m. The game's frame, its footprint test and its box fit all use that ring. The Erdgeschoss
sheet has nothing inside it; the Untergeschoss sheet's wing B has no door to the outside, and its only link to wing A lies 2.7 m beyond the
ring, so no front door exists in the drawing and none is invented (`--exit-snap` only moves a door that really leads out of the layout, and did not help).
Stepped solids need a frame from their real footprint before layouts can describe them.

## Sort evaluation and the local model

54 pages labelled by looking at their renders (`sort_labels.csv`: relative paths and types only). Rules alone 64.8 % (35 of 54; the rules were
tuned once against these labels, so that is optimistic). With `qwen/qwen3.8-27b` voting only on the 10 pages the rules were unsure of, 68.5 % (37 of 54):
three fixed (a section, a sector plan, a sheet), one broken (an arena floor plan called a seating plan). The corpus: 833 pages and images, 57
near-duplicates, one failure (a Basel Messe jpg of an unknown format; every PDF opens in PyMuPDF 2.1). The model takes 45-280 s a page, needs
at least 12000 tokens, invents room labels (so a label is kept only if the text layer has it) and its numbers are never used; bonsai-27b
mistook floor plans for access maps and is not used. Run it with `lms server start` and `lms load qwen/qwen3.8-27b --gpu max --context-length 16384 -y`;
afterwards `lms unload --all` and `lms server stop`. Results are cached per page content in `out/_cache/vlm.json`.

## Does not

Multi-storey stacking, stairs, windows, vector section or elevation geometry beyond the Tuilière measurements, OCR (no pass needed it), rooms of a
raster page, or any shipping of the derived data.
