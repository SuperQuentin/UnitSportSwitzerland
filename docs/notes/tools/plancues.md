# plancues: architectural PDFs into 2D plan cues (#1572, proof of concept)

`tools/plancues/` (Python, PyMuPDF + OpenCV + shapely + numpy, pinned in `requirements.txt`) reads the raw plans collected
for special buildings and writes one **cue** JSON per building. Raw documents (`X:/UnitSportSwitzerland/{Sport,Universities,Landmarks}`),
the venv, renders, traced outlines and every output stay outside the repo (`X:/UnitSportSwitzerland/plancues/`): the sources are
copyrighted. Only synthetic test PDFs (made by the tests) are in the repo. No game code is touched: exports are proposals.

## Commands (`python -I tools/plancues/plancues.py ...`, pages are 1-based)

- `sort <venue|all> [--vlm] [--labels sort_labels.csv] [--only-unsure]` -> `out/<venue>/sort_report.csv` (type, whether the classifier
  was unsure, rule, classifier and model types, duplicate_of, regions of a sheet, failures recorded and skipped). Types: floor_plan, orientation_plan, site_plan, section,
  elevation, seating_plan, access_map, mixed_sheet, photo, text.
- `orient <venue> <file> <page> [--region K] [--relax]` / `orient all`: orientation plans (visitor plans of flat colour rooms, see below): cue, footprint gate,
  review drawings. `all` takes every non-duplicate page the sort calls orientation_plan and writes `out/_all/orientation_report.csv`. `--relax` draws a near miss for
  looking (`out/_all/orient_review`), never a cue file.
- `scale|extract|place|run <venue> <file> <page> [--region K]`: scale (bar, then known object), walls/rooms/doors/outline, fit to
  the map; `run` does all of them and draws the review. Cues: `out/<venue>/cues/*.json`; debug rasters in `out/<venue>/debug_*`.
- `export <venue> house [--exit-snap M] [--gwr data.sqlite]|stadium`: `house-layouts_<cue>.json` (HouseLayout v2, written only if it validates, doors the plan lacks are inferred and listed in `house-layouts_<cue>_doors.json`, see Doors inferred; a Python mirror of
  `HouseLayout.Validate`, plus a `.report.json` of what was left out; **refused** when it keeps under 50 % of the cue's rooms or of their area, `export.keep_guard`) or `stadium_row.cs.txt/.diff/.json` (a `StadiumSpec` row and a diff
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
| UniS ground floor (p2) and UG sheet (p3, region 2) | Bar 0.2207 m/pt. 84 and 118 rooms, 37 doors. IoU with the solid's footprint 0.73 and 0.84 (rms 4.3 and 3.5 m): low_confidence. With inferred doors the EG exports 52 of 84 rooms (27 doors extracted, 26 inferred) but **fails `--houselayoutcheck`** (two doorways, see below); the UG keeps 45 of 118 and is refused (the format holds 60 rooms a floor). |
| Maladière `plan_01.jpg` | A faint scan of the parking level, no scale: rejected (IoU 0.50, the outline took the ramp and the caption). The PDF's plans are thumbnails in magazine pages (mixed sheets). |
| swissporarena site plan | Rejected as a schematic map before extraction. |

## UniS house layout: why it is a miss

**Before #1574** `BuildingOutline` took only the floor triangles at the solid's lowest height: a 422 m2 ring (28 x 16 m, the
south-east wing), so neither sheet fitted. **Since #1574** (`bldgframe.py` follows it) the base is the union of the floors at every
height: 3449.8 m2, frame 100.5 x 77.4 m at yaw 13.11 (swissBUILDINGS3D about 3455 m2).
- **Which sheet is which level:** the solid's floors lie at 559.7 m (south-east wing, 422 m2) and 562-564 m (the rest, about
  3030 m2). The Untergeschoss sheet covers 398 m2 of that wing and 2706 m2 of the rest; the Erdgeschoss sheet 7 m2 and 2732 m2. So the
  Untergeschoss is the solid's base, the format's level 0, and the Erdgeschoss the storey above.
- **What the export now does:** 98 (UG) and 72 (EG) rooms fit the footprint; 11 and 13 are dropped for corners, lying outside or
  overlapping. That drops their doors too (34 to 20, 32 to 13), and the rest are not linked: 85 of 87 (UG) and 56 of 59 (EG) rooms
  have no way in through the extracted doors. The greedy drop then ends at **one sliver room** each (a 3 m2 "corridor" with one
  door). `--houselayoutcheck` passes the UG export (that sliver plus a generated entrance passage about 40 m long: not a usable interior) and
  fails the EG one ("no ground floor room can be reached from the front wall, where the door leads in").
- **Since the inferred doors (#1572):** the rooms are reachable, so the greedy drop no longer ends at a sliver. What is left out is what the game refuses: 12 (EG) and 19 (UG) rooms outside the footprint in our tile, the format's 60 rooms a floor, and rooms whose largest piece in the game's strip cut is under 1 m (20 and 45).
  The EG layout (52 rooms) still fails the game's check on two doorways, "doorway without a matching cut on the other side": the entrance corridor's end wall and the wall of the office it opens on are 0.107 m apart (two corner heights under 0.15 m apart, which the game's strip cut merges), so the two cuts land 10 cm apart (limit 5 cm). Snapping all corner heights 0.16 m apart fixed that but dropped 30 more rooms. The UG has 118 rooms against a format limit of 60. Stairs between the two sheets are not tried (see Doors inferred).

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
`apply` on the 49 sure vector floor and site plans (4 already have a scale): **0 newly scaled**. 14 reach IoU >= 0.85 and fail the rms limit (rms 4-7 m on Hallenstadion,
UniLuzern, the swissporarena site plan), the scale-plausibility test (2-4 m per point) or the runner-up margin; the rest have no wall weight, no outline or IoU under 0.85.
`footscale draw <file> <page> [--region K] --relax` draws the overlay of a near miss (gate relaxed, for looking only).

## Orientation plans (visitor and wayfinding plans of the inside of one building)

`orient.py`, `pipeline.orientation_stage`, `orientcmd.py`. Such a plan has no scale bar and draws rooms as flat colour fills with names, entrances, stairs, lifts and
toilets: it used to be called an access map and rejected as "not geometry". It is a sort type of its own (`orientation_plan`; arrival, parking, campus and district maps are not).
- **Rooms**: the page is rendered (2000 px long side), its colours quantised (4-bit bins merged under Lab distance 6), and each colour's connected regions after an opening that
  removes text strokes and line work are rooms. Dropped: the paper (the colour of the page border), banners touching the border, round badges and small regions lying inside one
  other (pictograms), anything outside the biggest cluster of rooms (a legend). Specks inside a room (letters) are absorbed by it. The outline is the union of the rooms closed over
  the gaps between them. A room that surrounds others (a corridor) keeps them as holes.
- **Names**: words of the PDF text layer grouped into labels, attached to the room that holds the label's centre, or that a thin leader line from the label reaches (a chain of
  strokes under 1.6 pt whose far end is in exactly one room). A raster page (png, jpg) has no text layer and there is no OCR, so its rooms are unnamed. Stairs, lifts, toilets and
  entrances are marked only where a word says so (DE/FR/IT/EN); an entrance word within 8 % of the outline is placed on it. No pictogram is read.
- **Gate**: the outline goes through the footprint gate of any page with no bar (`footscale.judge`); the plan has no walls to be wrong about, so a passing plan is `low_confidence`
  at best (method `orientation_plan`, scale method `footprint_fit`), placed on the building. No door is drawn (doors are inferred at export, see Doors inferred), and a plan that fails the gate is a
  **topology-only cue** (`cue["topology"]`: rooms, names, relative areas, adjacency by contact length, entrances on the outline; coordinates are fractions of the plan's longest
  side, no metre anywhere).
- **Export**: a placed plan goes through the same house export; with no door drawn every door is inferred (all of them: the layout is low_confidence and the reason says how many).
- **Limits**: a white room on a coloured ground (paper rule), several floors on one page (the biggest cluster only), isometric stacks (rooms found, shape never passes the gate),
  names beside the plan with no leader line, partial plans (the Kunsthaus plans leave the white rooms out, IoU 0.7 to 0.8).

54 pages (non-duplicates the full sort calls orientation_plan): 4 placed, all UniLuzern `UniLU_floor_plans_{de,en}` p5 and p7 (the 1. OG and 1. UG of the Uni/PH building, 50 and 34
rooms, 7 and 14 named, IoU 0.98, rms 0.75 m, but a near-rectangle: only its aspect ratio 2.04 / 2.07 supports the scale). The other 50: IoU under 0.85 (23), IoU fine and boundary rms over
2 % of the footprint's size (17: Uni Mail, Bastions, UniLuzern 4. OG and EG), a plain shape a box fits as well (5), a twin building (1), under 3 colour regions (3), no position hint (1).
2690 rooms in all, 215 named, 26 pages with a name. The four placed plans now export with inferred doors (UniLU de/en p5: 40 of 50 rooms, p7: 30 of 34) and pass `--houselayoutcheck`.
Same building, both kinds of plan: only the Uni/PH building in Lucerne (the Bauwelt article's `Bauwelt_2012_33_plans` p4 Erdgeschoss, region (80, 60, 480, 295), passes the gate at
IoU 0.98, `ok` by its written 1:1000, against the orientation plan `UniLU_floor_plans_de` p6, a near miss drawn with `--relax`, IoU 0.96, rms 2.06 m): both sit on EGID 213248, outlines
IoU 0.96, placements 5.7 m apart. Of the 11 numbered keys of the Bauwelt plan, the Free Flow and Gästebereich keys fall in the room named Mensa, three of five Hörsaal keys in rooms named
Hörsaal 6, Hörsaal 8 and the Auditorium corridor; the Foyer and Küche keys fall in unnamed rooms, Eingang and Poststelle in none (the offset above). A scratch comparison, not a command.

## Doors inferred (`doors.py`, `strips.py`, `export.house_layout`)

A plan with no door (orientation plans) or too few reaches only a few rooms. At export the missing doors are inferred, marked and counted; extracted doors, entrance arrows and entrance words are never touched.
- **Circulation space**: a room named Gang, Korridor, Flur, Foyer, Erschliessung, Treppenhaus, Eingangshalle, couloir, vestibule, corridoio, corridor, lobby..., or "hall"/"Halle"/"Treppe" at the start of a name; unnamed: long and thin (short side under 3.5 m, ratio over 3.5) with 3 neighbours or more, or the largest room with 5 neighbours or more; the parts of a cut room (below).
- **Placement**: the fewest doors that reach every room, spreading from the front door. A group of rooms joined by extracted doors but unreachable gets one door, on the longest wall (1.2 m or more) shared with a circulation room already reachable, else on the longest shared wall with any reachable room (0.8 m or more); circulation spaces first. A door is 0.9 m, 1.1 m between two circulation spaces or into a room over 120 m2, centred on the wall and 0.3 m from the corners (0.15 m on a 1.2 to 1.5 m wall). Windows and openings are not read from the plan, so a wall drawn as glazing is not avoided. Only a wall along an axis of the layout and exactly (2 mm) on both rooms takes a door.
- **Front door**: a real entrance (arrow, word, gap) when the plan shows one. Otherwise on the outer wall of a circulation room (1.5 m or more) nearest the building's GWR entrance point (`data/gwr/data.sqlite`, table `entrance`; the EGID of the tile matches: UniS EDID 0 at 2599750.3 / 1199903.1, Uni/PH Lucerne EDID 0 at 2666387.5 / 1211322.6). With no GWR point: the longest outer wall (the road data of the tiles is not read: RoadCodec is large and both buildings have a point).
- **Marking**: every inferred door has `source: "inferred: <rule>"` and a confidence (0.5 beside circulation space or at a GWR point, 0.35 beside an ordinary room, 0.3 for a cut) in the cue's `openings` (plan coordinates); `levels[0].extra.inferred_doors` counts them; `house-layouts_<cue>_doors.json` lists every door, extracted or inferred, in the layout's frame (the game format has no source field). Over 70 % inferred: still exported, the cue is low_confidence and says "N of M doors are inferred".
- **What the export had to learn to keep the rooms** (UniS lost 13 of 98 rooms before; each fix is a rule of the game or of the format, `--houselayoutcheck` names it):
  - corners of one room that lie on a neighbour's wall are added to it and everything is rounded to the millimetre together (rounding each room alone to 12.5 cm moved shared walls apart by 9 cm: 5 of 122 left); corners closer than 13 cm are one; corners on a straight wall are dropped again (the 64-corner limit); gaps up to 1 m between rooms are closed, the gap going to one room;
  - the plan is turned about its middle to be square to the building's box when it is within 10 degrees of it (the game draws a layout square to its box; UniS was 2 degrees off, which made a staircase of strips), and near-vertical and near-horizontal walls are made exact (`rectify`);
  - a room that surrounds others has holes, the format has none: it is cut by straight lines through the holes into parts, which tile the same space and are joined by an inferred door (Lucerne p5: rooms 0, 2, 3);
  - the game plans inside a 120 m box (`BuildingFootprint.MaxSide`; the Lucerne building is 132.7 m long: what lies beyond is cut off), 60 rooms a floor (the smallest go), and refuses a room whose largest piece in its strip cut is under 1 m a side. `strips.py` is a port of that cut (`RoomStrips.Cut`) used to name and drop such rooms (it ignores the other rooms' corners, so it can miss one).
- **Result** (`--houselayoutcheck` with the build of #1574, plans in `out/<venue>/check/`): Uni/PH p5 (40 of 50 rooms, 517 rooms in the game after its cut, 50 doors, all inferred) and p7 (30 of 34, 42 doors) pass, `de` and `en` twins alike. UniS EG fails as above, UG is refused.
- **Not done**: two levels of UniS joined by stairs. The UG sheet is the solid's level 0 and the EG the level above, but the format then wants the street door on the UG, no Treppe or Treppenhaus is named on both sheets, and the 60-room limit already refuses the UG.

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
**orientation_plan** (added later): 42 train pages (and 2 of the first 54) and 6 held-out pages relabelled or labelled from the renders (floor_plan, access_map, site_plan, mixed_sheet before; the
held-out six were relabelled, nothing was tuned on them). Retrained: held-out 78 of 90 = 86.7 % (was 87.8 % on the same pages with the old labels), orientation_plan precision 71.4 %
(5 of 7), recall 83.3 % (5 of 6; a sample of 6); floor_plan recall 75 %, precision 92 % (two floor plans read as orientation plans). Full sort: orientation_plan 60 pages (54
non-duplicates); floor_plan 138 -> 93, access_map 82 -> 73, mixed_sheet 137 -> 130, seating_plan 49 -> 48, site_plan 47 -> 53, unsure 143.
Run: `lms server start`, `lms load qwen/qwen3.8-27b --gpu max --context-length 8192 -y`; afterwards `lms unload --all`, `lms server stop`.

## Does not

Multi-storey stacking, stairs, windows (a wall drawn as glazing is not avoided by an inferred door), vector section or elevation geometry beyond the Tuilière measurements, OCR (no pass needed it), rooms of a
raster page, or any shipping of the derived data.
