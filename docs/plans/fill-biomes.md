# Fill biomes: the earth fill outside Switzerland (#1809)

Status: **planned**. Phase 1 (the seam and the photo guess) on `feat/1809-fill-biomes`.

## The problem

Under `--frame earth`, every leaf the regions do not hold is generated ("fill", `terrain/fill-on-pyramid`,
`terrain/producers-on-leaves`). The fill's relief follows the real Earth, because it stands on the `cube_global`
pyramid. Its cover, trees and buildings come from the generator built for Switzerland (`terrain/generated-relief`).

Past the generator's own map (550 x 430 km round Switzerland, `swiss_relief.gz`), that map's edge is extruded: no
rivers, no villages, no roads, but the same green open ground, the same conifer forests and solitary trees, and
farms on a jittered 350 m grid. The first flight far away found green grass with trees in the Sahara (`LV95
1829400,-515818`, beige from orbit) and grass over open sea (`test_output/asia-flight/` in the main checkout).

## The user's decision: both

1. Real global land-cover and climate data first.
2. Where it is missing (not built, not shipped, or no value at that point): a guess from what the pyramid already
   has, its satellite colour, the latitude and the altitude.
3. Below that, today's Swiss generator.

## The fallback chain

`FillBiome.At(lat, lon, altitude)` returns one `Biome` per point, by the first step that decides:

| Step | Decides | Gives up when |
|---|---|---|
| 0. Swiss guard | `Swiss` inside the generator's own map (LV95 2385000-2935000 / 974000-1404000, less a 20 km band where the guard is dithered out). There the generator is shaped on the real country, and the Swiss controls (Lavaux, the Salève fill) stay byte-identical. | outside the map |
| 1. Land cover (phase 3) | the class of the pyramid's land-cover section at the fixed shared level, sharpened by the climate zone (Köppen-Geiger): e.g. grassland in BWh is desert grass, in ET tundra, in Cfb temperate. | the section is not built or not shipped, or the point is NoData |
| 2. Photo guess (phase 1) | from the shipped globe colour, the latitude and the altitude (below) | the photo-statistics grid is missing |
| 3. Swiss generator | `Swiss`: today's fill, unchanged | never |

After step 1 or 2, two altitude rules apply. They depend on the latitude, not on Switzerland:

- **Over the snow line** (`SnowLineM(lat)`: 4,900 m at the equator, 5,500 m at 25°, 4,800 m at 35°, 2,900 m at 46°,
  1,500 m at 60°, 600 m at 70°, 200 m at 80°; linear between): `Ice`.
- **Over the tree line** (`TreeLineM(lat)`: 3,800 m, 4,000 m at 25°, 3,300 m at 35°, 2,100 m at 46°, 900 m at 60°,
  300 m at 68°, 0 at 72°): `Tundra` (alpine), unless the step gave `Desert` (cold high deserts stay bare).

At 46°, both lines are the Swiss generator's own (`CoverPalette.SnowLine` 2,900 m, no field trees over 1,700-2,100 m).
The two rules therefore agree with the generator at the guard's edge.

### The photo guess

The input is a photo-statistics grid baked once from the **shipped** globe colour (`assets/globe`, levels 0-2, all
12 months of Blue Marble NG; ~10 km a pixel at level 2). The grid is embedded in the assembly as
`src/Terrain/Biomes/photo_stats.bin`. That makes it:

- Godot-free: BlendCheck and the tests read it; WebP needs Godot's `Image`.
- identical on every peer;
- independent of a deeper pyramid on D:. Over Switzerland, D:'s colour levels go to 13 and the shipped ones stop at 2;
  reading D:'s levels would make the server's fill differ from a client's.

The grid has 0.2° cells (1800 x 900). Each cell holds 4 bytes:

- the sRGB of the greenest month (the "summer" colour: the most `G - (R + B)/2`, mean over the cell);
- the number of snowy months (bright, near-neutral, luma over 0.62 with `R - B` under 0.06: bright sand is warm,
  snow is not);
- a sea flag (water in every month, `GlobeGround.Classify`'s water weight over 0.5).

At run time the guess reads the grid bilinearly at the point's latitude and longitude, warped by ~0.1° of
deterministic value noise (+, -, *, floor only: no transcendental function, so Windows and Linux peers agree to the
bit). It then classifies the colour:

- sea flag: `Sea`;
- 10 or more snowy months: `Ice`;
- |lat| ≥ 55°, 6 or more snowy months, and pale or barely green: `Tundra`;
- barely green, warm, bright: `Desert`;
- barely green, warm, middling: `Steppe` (|lat| > 25°) or `Savanna`;
- |lat| ≤ 25° and dark green: `TropicalForest`; otherwise in the tropics: `Savanna`;
- |lat| ≥ 48°, 3 or more snowy months, dark: `Boreal`;
- anything else: `Swiss` (temperate grass and forest, the generator's own look).

The thresholds are tuned on the test spots below and pinned by `FillBiomeTests`.

The guess cannot see cropland, towns or wetland. Only land cover gives those (phase 3).

## Biomes

What each biome changes in a fill leaf. The change is a post-pass on the generator's leaf (`GeneratedLeaves.Build`,
before `OnPyramid`). It reads the biome at each vertex for cover, at each tree's place for trees, and at a farm's
350 m cell for buildings, so a farm goes or stays whole.

| Biome | Ground (`Open` and cultivated becomes) | Generator's forest | Trees | Farms | Water | Globe (same classes) | Later (phase 4) |
|---|---|---|---|---|---|---|---|
| Swiss | unchanged | unchanged | unchanged | unchanged | unchanged | unchanged | |
| TemperateGrass, TemperateForest, Cropland (land cover only) | unchanged, the Swiss look | unchanged | more or fewer by the data's tree cover | unchanged | unchanged | unchanged | fields by the data's cropland share |
| Desert | `Sand` (new) | `Sand` | 1 in 50 kept as a low shrub | none | the generator's rivers dried (wadis) | open weight drawn as `Sand` | dunes in the relief detail, oases (palms, a village by the water), regional houses |
| Steppe | `DryGrass` (new) | `DryGrass`, 1 in 6 trees kept | shrubs and solitary trees | 1 in 6 | kept | `DryGrass` | yurts, regional houses |
| Savanna | `DryGrass` | `OpenForest` | 1 in 4 kept, broadleaf solitary | 1 in 4 | kept | `DryGrass`, forest as `OpenForest` | acacia model |
| TropicalForest | `LushGround` (new) | unchanged | broadleaf only, trees added to forest density | 1 in 5 | kept | forest weight up | broadleaf and palm models |
| Boreal | unchanged | unchanged | conifer only, trees added | 1 in 5 | kept | forest darker | birch, spruce models; lakes from land cover |
| Tundra | `Tundra` (new: olive-brown moss and lichen) | `Tundra` | 1 in 30 kept as a dwarf shrub | none | kept | `Tundra` | |
| Ice | `Glacier` | `Glacier` | none | none | none | snow weight 1 | crevasses |
| Wetland (land cover only) | `Wetland` | `Wetland` | 1 in 3 kept | 1 in 3 | kept | `Wetland` | reeds, pools |
| Urban (land cover only) | unchanged | unchanged | unchanged | unchanged | unchanged | grey | towns abroad |
| Sea | owned by the sea work (`asia-globe-over-fill`): fill leaves on the ocean become sea | | none | none | theirs | theirs | |

Further rules for these biomes:

- **Rock, scree, boulders and glaciers** stay the generator's in every biome but Ice.
- **Built cover** (paving, parking, gardens) goes with the farms it belongs to. In a biome without farms it becomes
  the biome's ground.
- **Roads and villages**: the generator only makes them inside its own map, under the Swiss guard. Abroad there are
  none to change until a generator for abroad exists (later).
- **New ground classes**: `Sand` 42, `DryGrass` 43, `Tundra` 44 and `LushGround` 45 (`CoverClass`) get colours in
  `CoverPalette`. They are exempt from its fixed 2,900 m snow line: their biome already applies the latitude's snow
  line. Footstep sounds (`Audio/Surfaces`), birds and other cover readers get a row or the nearest class.

## Data (phase 3)

| Dataset | Resolution | Licence | Download | Use |
|---|---|---|---|---|
| **ESA WorldCover 2021 v200** | 10 m | CC BY 4.0 ("© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium") | AWS open data `s3://esa-worldcover`, no account; 3° x 3° COG tiles, ~100+ GB in all | **first choice**: 11 classes incl. bare/sparse, snow/ice, moss/lichen, mangroves, herbaceous wetland, built-up, cropland, tree cover. The COGs' internal overviews allow a coarse read without downloading the full resolution. |
| Copernicus Global Land Service LC100 v3 (2019) | 100 m | CC BY 4.0 (Zenodo) | Zenodo, no account; one global GeoTIFF per layer | **fallback or complement**: per-class cover fractions (tree, shrub, grass, crop, bare, snow, water, urban). They give densities directly. |
| MODIS MCD12Q1 v061 / MCD12C1 | 500 m / 0.05° | NASA data, no restriction (citation asked) | needs an Earthdata account (the user's) | alternative if the two above fail |
| ESA CCI Land Cover / C3S | 300 m | CC BY 4.0 / Copernicus licence | C3S needs a CDS account | not needed |
| **Köppen-Geiger, Beck et al. 2023** (1991-2020) | 1 km (also 0.083° and 0.5°) | CC BY 4.0 | figshare / gloh2o.org, no account | climate zone: splits grassland into desert grass, steppe, savanna, temperate meadow, tundra; trees into tropical, temperate, boreal |

### Resolution actually needed

- A fill leaf is ~0.6 km and the fill domain reaches 40 km round each player. A biome changes over tens of
  kilometres, so ~1.2 km (the pyramid's level 4 lattice) would show structure inside the domain. ~5 km (level 2)
  still draws the regional picture, with dithered edges.
- The globe draws levels 0-2 from orbit and finer ones near the ground. Its palette reads the same biome per vertex,
  so it never needs more than the fill.

### Into the pyramid

The data goes in as a new section, built by the TerrainPreprocessor like the ETOPO heights (`tools/cube-tiles`):

- the tool: `--cube-landcover --worldcover DIR --koppen FILE --cube-out cube_global --cube-levels 0-4`;
- per pyramid tile, a `Biome` lattice (`CubeKey.LatticeBits`, 513² vertices): per vertex the class (u8), the tree
  cover (u8, %) and the Köppen zone (u8), each from the source's pixels in the vertex's footprint (majority class,
  mean cover), a function of the lattice point (the seams rule of `cube-tiles`);
- encoded like the leaves' `Cover` section (bits, Brotli), in `.cubv` bundles by level, listed per level in the
  manifest (`biome`), as colour is.

### Size against a release

Estimated before measuring; phase 3 measures it. Per vertex 3 bytes. The ocean (70 %) costs next to nothing, and the
classes compress well.

| Levels | Spacing | Tiles | Estimate | Where |
|---|---|---|---|---|
| 0-2 | ~4.9 km | 126 | 5-10 MB | **shipped** with `assets/globe` (Git LFS, 23.5 MB today) |
| 3 | ~2.4 km | 384 | 20-40 MB | streamed with #1656 |
| 4 | ~1.2 km | 1,536 | 80-160 MB | streamed with #1656 |

- **The fill reads land cover at one fixed level** (`FillBiome.LandCoverLevel`, 2 at first), never "the finest
  there is". A pyramid with deeper levels (D:, the NAS) must not change a leaf.
- Raising the level is a content change. It goes with #1656's streaming and a protocol bump, so old and new peers
  never meet.
- If the 0-2 levels came out over ~50 MB, that would be a question for the user.
- The photo grid (phase 1) is ~1-1.5 MB in the assembly. The Licenses page (`core/licenses`) gets a row for ESA
  WorldCover and one for Beck et al. in phase 3. Blue Marble is already credited.

## Determinism

The server and every client build the same fill leaf (`Terrain/Earth/GameLeaves`). Every biome input is therefore:

- part of the build (the photo grid in the assembly, the code's rules and noise), or
- in the shipped levels (land cover at the fixed level), or later streamed from the server with the rest of the
  pyramid (#1656);
- never something only one side has, such as D:'s deeper levels or a local download.

The rest of the determinism rules:

- **Arithmetic**: the biome decision uses +, -, *, / and floor only, on doubles. A tree's or farm's keep/drop is a
  hash of its lattice cell, not a random draw.
- **Protocol**: no protocol change here. D2 (#1727) bumps to 96. If this lands before D2, D2's bump covers it; if
  after, the change in fill content needs the next bump, noted in the PR.

## Coordination

- **The sea**: `asia-globe-over-fill` makes fill leaves on the ocean sea. This plan only reports `Sea` and keeps
  trees and farms off it; the water is theirs. Check their PR before phase 2.
- **The globe's colour classes** ("bright sand read as snow", the 20 km handover): #1802 / PR #1803. Phase 2 (the
  globe agrees) starts after it merges and edits `GlobeGround.Colour` only.
- `Globe.cs` is being edited by both sessions above: not touched here.

## Phases and PRs (each one shippable)

1. **The seam and the photo guess** (`feat/1809-fill-biomes`):
   - the `Biome` set and `FillBiome.At` (the guard, an empty land-cover slot, the guess, the Swiss floor);
   - the photo grid and its Godot bake (`--biomephotobake`);
   - the post-pass in `GeneratedLeaves`;
   - the four new ground classes;
   - `BlendCheck --fill-biomes`: top views of fill leaves at the test spots and the controls' digests;
   - `FillBiomeTests`.
2. **The globe agrees**: `GlobeGround.Colour` paints its open, forest and snow weights by the vertex's biome (the
   same `FillBiome.At`), so the handover from fill to globe keeps its colour. After #1803.
3. **Land cover in the pyramid**: the preprocessor stage, the shipped levels 0-2, chain step 1 live, the Licenses
   rows. The guess stays for NoData and for builds without the section.
4. **Biome looks, one PR each**:
   - desert: dunes, oases, scrub, palm and cactus models (`[Showcase]`);
   - tropical: broadleaf and palms;
   - boreal: birch and spruce;
   - savanna: acacia;
   - tundra;
   - then regional houses and villages abroad;
   - the snow line by latitude for gameplay (skis glide over 2,900 m everywhere today).

## Checks

`BlendCheck --fill-biomes` builds fill leaves at each spot, on the pyramid from D:. It writes a top-view PNG
(cover colours, trees, buildings) and prints the biome shares. The spots:

| Spot | Where | Expect |
|---|---|---|
| desert | `LV95 1829400,-515818` (the user's) | Desert |
| steppe | Kazakhstan, 48.5° N 67° E | Steppe |
| tundra | Yamal, 69° N 70° E | Tundra |
| ice | Greenland, 72° N 40° W | Ice |
| jungle | Amazon, 3° S 62° W | TropicalForest |
| sea | mid-Atlantic, 30° N 40° W | Sea |
| controls | Lavaux 2547200,1147800, the Salève fill | Swiss, **byte-identical** to `main` |

## Decided without the user (review)

- **The Swiss guard**: the generator's own map is `Swiss` whatever the data says. There the generator is shaped on
  the real country, and the controls stay identical. Land cover could later refine it (cropland vs forest) if wanted.
- **The photo guess is a baked grid in the assembly**, not the pyramid's colour read at run time. That keeps it
  deterministic (D:'s deeper colour levels differ from the shipped ones) and Godot-free (tools and tests).
- **Phase 1 changes no relief**: dunes and the like come in phase 4. The height stays the pyramid's plus the
  generator's short relief, so collision is unaffected.
- **ESA WorldCover over MODIS**: no account is needed, CC BY 4.0, and finer. MODIS would need the user's Earthdata
  account.
