# Entertainment venues: where bowling centres, cinemas, arcades, restaurants and bars are (#1233, epic #1232)

Not the concert venues (`venues`, #948). Research per kind: `venue-bowling`, `venue-cinema`,
`venue-arcade`, `venue-dining`.

- **No dataset we build from says "cinema".** swissBUILDINGS3D draws a solid, the GWR files it as
  commercial (or civic, or flats: Lausanne's Pathé Galeries is in an `Apartment` solid). OpenStreetMap
  names them: `amenity=restaurant|cafe|fast_food|bar|pub|nightclub|biergarten|cinema`,
  `leisure=bowling_alley|amusement_arcade` (`VenueType`; stored as numbers, never renumber).
- **Offline stage**: `TerrainPreprocessor --venues src/Interiors/venues.tsv.gz --osm <switzerland .osm.pbf>
  [--out <chunk dir>]` (~45 s). Points as they are, closed outlines by their centroid (or the inside
  sample nearest it), Switzerland's box only. With `--out`, each point is checked against the built
  `.bldg` tiles (`VenueFix`): `Inside` a solid's footprint, `Snapped` onto the nearest solid within 12 m
  (the centre of its biggest roof triangle, as castles do), `Open` (tiles there, no solid near: park
  kiosks, beer gardens) or `Raw` (no tile there: kept, still matched by containment). It writes no
  tiles, so it skips the manifest hash pass.
- **The file** (`VenueFormat`, gzipped TSV, byte-identical reruns): `# venues v1 <source>`, the column
  names, then `osm type e n fix flags count cuisine name` sorted by E, N, OSM id. `flags`
  (`VenueFlags`): outdoor seating yes/no, billiards, table football, darts, pinball, live music,
  karaoke, ninepins. `count`: OSM `lanes` or `screen` when mapped. Embedded in the game assembly
  (`UnitSportSwitzerland.csproj`, like `swiss_relief.gz`): on the server and every client from boot,
  never sent, never late, so a tile's building types cannot change under its cache. ODbL like
  `Castles.List.cs` (`tools/osm-odbl-licence`).
- **Numbers** (Oct 2026 extract, 825 local tiles on D:): 29,786 venues: restaurant 18,642, café 3,840,
  fast food 3,195, bar 2,573, pub 756, nightclub 301, biergarten 116, cinema 273, bowling 55,
  arcade 35; 678 KB. Fix: raw 26,566 (outside the local tiles), inside 2,655, snapped 198, open 367:
  inside the tiles 82 % of points already lie on a solid. Flags are rare: outdoor 6,370, no outdoor
  904, billiards 19, darts 15, table football 11, ninepins 9, pinball 0, so games come from the venue
  type (`venue-dining` gives odds), not from the data.
- **Matching** (`Venues.Match`/`Contained`): a solid is a venue when its real footprint
  (`Building.Covers`) holds the point, found through a per-kilometre index of the 3x3 tiles round the
  tile (`Reach` 300 m): exact, unique, a pure function of the tile like `ikea` and `castles`.
  `BuildingTypeMap.VenueOf(i)`; the main door carries it as `DoorSpot.Venue` (index + 1, 0 none).
  Not a `BuildingGroup`: churches, landmarks and stored plans are untouched, and each kind's own
  sub-issue decides how its building is planned.
- **Several venues in one solid** are the rule in towns: a city block or a mall is one solid. Local
  tiles: 2,853 venues in 1,632 buildings, 1,221 sharing one with a higher-ranked venue. `Rank`:
  bowling > cinema > arcade > nightclub > pub > bar > restaurant > biergarten > café > fast food; a tie
  keeps the first in the list. The building shows the winner; `Contained` still lists them all, which
  ground-floor dining units will need (sub-issue 5). Arena la Praille (Geneva) shares its 326 x 123 m
  mall with Bowling de la Praille; the Cinémathèque suisse its building with Le Cinématographe.
- **Overlapping solids** in the data: 3 points are covered by two solids in neighbouring tiles; both
  show the venue, and peers still agree.
- **Neon signs** (`NeonSign`, `VenueSigns` on `TileFurnished`, client only, drawn to 250 m): the OSM
  name (cut at a word past 26 characters; no name: the type's word, `Venues.GenericName`) in Saira at
  weight 560 (the variable font's default is a hairline), gas colour pushed over 1, a thin translucent
  outline; beside it a generic icon in tubes (pin and ball, film reel, joystick, cup, cocktail glass,
  beer mug, burger, pizza slice, fork and knife: `NeonSign.Icon`, never a logo), flat ribbons with a
  whiter core (`neon_tube.gdshader`), joint discs a hair behind so bends do not bead. Cartoon and PS1
  have no bloom (only Realistic does), so a sign throws its own light: additive ellipses on the wall
  behind the name and the icon (`neon_halo.gdshader`), faint by day, full at night through the
  `world_night` global (halo strips along the tubes spiked where segments overlapped: dropped).
  `BankSigns` leaves a venue's door to it. Hung 0.75 m over the door, 0.10 m off the wall; letters
  0.42 m (0.6 m on a hall), the whole capped at 3.5 door widths. Model viewer: `--modelsonly "Neon sign"`.
- **Check**: `--venuecheck --systems ui [--chunks <dir>]` (`VenueProbe`): the list (count, every type,
  sorted, in the box), the rules on a hand-made tile (a mall with a cinema and a restaurant is the
  cinema, a café in a house, nothing outside, a neighbour's solid takes nothing, the main door carries
  the venue, every type's sign and icon build), and on real tiles: every venue the stage placed on a
  solid is covered by one (2,853/2,853), each hall listed OK/MISS with its solid and its door in
  LV95. Shots at Lausanne's Flon (origin 2537000,1152000, `--time 22`): Bowl Bar Flon
  `701.5,g1.7,-539.4,8,69.6,6,<png>`, Pathé Flon `695.8,g1.7,-583.9,12,339.6,3,<png>`.
- **Regenerate** after an OSM refresh or a building rebuild (only the snapped points depend on the
  tiles), ideally with the national tiles so every raw point is checked.
- **Not here**: the generated world has no venues (a generated tile cannot be told from a real one
  at a building's level yet); interiors, facades, terraces, music and staff are sub-issues 2-6 of #1232.
