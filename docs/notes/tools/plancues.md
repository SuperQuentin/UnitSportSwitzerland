# plancues: architectural PDFs into 2D plan cues (#1572, proof of concept)

`tools/plancues/` (Python, PyMuPDF + OpenCV + shapely + numpy, pinned in `requirements.txt`) reads the raw plans collected
for special buildings and writes one **cue** JSON per building. Raw documents (`X:/UnitSportSwitzerland/{Sport,Universities,Landmarks}`),
the venv, renders, traced outlines and every output stay outside the repo (`X:/UnitSportSwitzerland/plancues/`): the sources are
copyrighted. Only synthetic test PDFs (made by the tests) are in the repo. No game code is touched: exports are proposals.

## Commands (`python -I tools/plancues/plancues.py ...`, pages are 1-based)

- `sort <venue|all> [--vlm] [--labels sort_labels.csv] [--only-unsure]` -> `out/<venue>/sort_report.csv` (type, whether the classifier
  was unsure, rule, classifier and model types, duplicate_of, regions of a sheet, failures recorded and skipped). Types: floor_plan, site_plan, section, elevation,
  seating_plan, access_map, mixed_sheet, photo, text.
- `scale|extract|place|run <venue> <file> <page> [--region K]`: scale (bar, then known object), walls/rooms/doors/outline, fit to
  the map; `run` does all of them and draws the review. Cues: `out/<venue>/cues/*.json`; debug rasters in `out/<venue>/debug_*`.
- `export <venue> house [--exit-snap M]|stadium`: `house-layouts_<cue>.json` (HouseLayout v2, written only if it validates; a Python mirror of
  `HouseLayout.Validate`, plus a `.report.json` of what was left out) or `stadium_row.cs.txt/.diff/.json` (a `StadiumSpec` row and a diff
  against `StadiumCatalog.cs`, which is never edited).
- `review <venue>`: the cue over the swissimage aerial (3 px/m, the WMS of `terrain/stadiums`) and over its source page.
- `python -I evalsort.py sort_labels_holdout.csv [--field rule_type|model_type|type]`: confusion matrix, precision and recall against labels.
- Tests: `cd tools/plancues && python -m unittest` (synthetic PDFs, no X: needed). `requirements.txt` adds scikit-learn.

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

## Scale from the footprint (vector pages with no bar and no known object)

`footscale.py`, `pipeline.footscale_stage`, `plancues.py footscale validate|apply|draw`. The outline (extracted at a provisional scale that is fed back
until it settles) is searched against the 12 nearest footprints within 120 m of the venue hint (`place.scale_search`, shared with the scan stage: coarse
then fine scale x every turn, IoU, parabola on the fine steps). The gate: IoU >= 0.85 and boundary rms <= 2 % of sqrt(footprint area); the best building
beats the next by 0.05 IoU (a runner-up with the same scale +-3 % does not count against the scale); the plan beats its own minimum rectangle and convex
hull on that footprint by 0.03 IoU (else only an elongated near-rectangle, aspect >= 1.3, whose two aspect ratios agree within 4 %, passes, as low_confidence);
neither is a part of the other (plan cover minus footprint cover within 0.12); a plausible scale (0.015-1.5 m per point). A written ratio ("1:200", the PDF's
page size is the paper size) within 5 % of the result makes it `ok`, otherwise `low_confidence`; method `footprint_fit`. Door gaps and stair treads were
not used: they pin a scale to +-20 %, not 5 %. Venue hints for the 15 venues with plans are in `venues.json` (swisstopo geocoder).

Validation (hide the bar, compare; `footscale validate`): only **3 pages** have a bar that is really the drawing's (the outline at the bar's scale fits a footprint,
IoU >= 0.75) and a building that is in our tiles: vonRoll p24 -0.3 % (IoU 0.96), UniS p3 region 2 -1.8 % (IoU 0.83), vonRoll p27 -6.7 % (IoU 0.77). Mean -3 %, the
footprint scale runs **small**, not large (overhangs would make it large); three pages cannot support a correction, so `BIAS = 1.0`. The gate passes none of them
(vonRoll p24: the next building fits 0.95 against 0.96, and a box fits as well; UniS: a stepped complex, rms 5.6 %), so 0 accepted, 0 wrongly accepted. One more
page is confirmed by its written ratio: UniLuzern `Bauwelt_2012_33_plans` p4 region 1, 1:1000 against the fit's 0.3536 m per point (-0.2 %), its bar belongs to
another drawing. Thresholds follow these pages and the failures seen, not a fitted optimum: too few pages for more.
`apply` on the 49 sure vector floor and site plans (4 already have a scale): **0 newly scaled**. 17 fail only the rms limit or the scale-plausibility test with IoU
0.84-0.97 (Hallenstadion 9, UniLuzern 4: the footprint's outline is not the plan's, rms 4-7 m); the rest have no wall weight, no outline or IoU under 0.85.
`footscale draw <file> <page> [--region K] --relax` draws the overlay of a near miss (gate relaxed, for looking only).

## Sort: method and honest numbers

Three label sets, kept apart: `sort_labels.csv` (54, the first look, which the old rules were tuned on), `sort_labels_train.csv` (222, two random draws by
folder and venue) and `sort_labels_holdout.csv` (90, drawn at random with seed 1572 before any rule changed, never trained or tuned on, read once).
The last two were labelled by Claude from the renders (`labelled_by`; `ambiguous_with` names a second acceptable type): a human should spot-check them.

Method (`sortmodel.py`): per-page features (vector paths and orthogonal share, stroke weights, raster flat colours and soft gradients, Hough lines,
closed rooms, vivid blobs, text and picture cover, scale ratios, FR/DE/IT/EN keywords of text and file name; `features.VERSION` keys the cache) feed a
mean of a logistic regression and a random forest (scikit-learn, no torch). Under 0.65 best probability the page goes to `qwen/qwen3.8-27b` with reasoning
off (`reasoning_effort: none`, 1000 px, a hint line of word and picture counts): 2.5 s a page against 45-280 s with reasoning, cached per content.
The type is the classifier's probabilities plus 0.5 for the model's type; `unsure` = the two disagree (or no vote and under 0.55). **Use floor_plan,
site_plan and section only when not unsure.** `evalsort.py <labels>` prints the matrix, precision and recall.

| Labels | Hand rules | Classifier | Classifier + model |
|---|---|---|---|
| 90 held-out | 47.8 % | 77.8 % | **87.8 %** strict, 94.4 % counting the second acceptable type of 26 ambiguous pages |
| 54 + 222 (6-fold CV by file) | 51.8 % (48.6 % on the 222 unseen) | 67.4 % | 83.7 % |

On the held-out set, pages not unsure (69 of 90) are 94.2 % right; sure floor_plan 12 of 12, site_plan 6 of 6, section 4 of 4 (recall 57 %, 50 %, 100 %: a
missed plan costs less than a wrong one). An audit of 12 fresh sure geometry pages found none wrong. The rules scored 64.8 % on the 54 they were tuned on.
Samples are small (4 sections, 4 elevations): treat a type's percentage as rough. Costs per page: rules 0.3 ms, classifier 33 ms, features 0.19 s, model 2.5 s.
All 833 pages (57 duplicates, 1 unreadable jpg), before -> after: photo 309 -> 111, text 191 -> 192, floor_plan 81 -> 138, site_plan 77 -> 47, access_map 62 -> 82,
mixed_sheet 59 -> 137, section 41 -> 53, elevation 8 -> 24, seating_plan 5 -> 49; unsure 309 -> 145. Remaining misses: site plan against access map
(`2022_hsg-campusplan-20211221.pdf`), a small section on a large page called mixed_sheet (`Wankdorf_sections_axo_roofplan.pdf`), text pages with one picture called
mixed_sheet (`Cathedrale_teaching_dossier_plan.pdf`), historic St Gall plans (`Plan_of_Saint_Gall_recto_full.jpg`, labelled site plan, floor plan by the model).
The model invents room labels (a label is kept only if the text layer has it); its numbers are never used; bonsai-27b mistook floor plans for access maps and is not used.
Run: `lms server start`, `lms load qwen/qwen3.8-27b --gpu max --context-length 8192 -y`; afterwards `lms unload --all`, `lms server stop`.

## Does not

Multi-storey stacking, stairs, windows, vector section or elevation geometry beyond the Tuilière measurements, OCR (no pass needed it), rooms of a
raster page, or any shipping of the derived data.
