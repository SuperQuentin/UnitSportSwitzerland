# Concert venues across Switzerland (#948)

33 rows in `StadiumCatalog`: Stade de Genève (#885) and 32 researched for #948, of three kinds
(`StadiumSpec.Kind`, `VenueKind`):

- **Stadiums** (an open bowl): Wankdorf (Bern), St. Jakob-Park (Basel), Letzigrund (Zürich), kybunpark
  (St. Gallen), swissporarena (Luzern), Stockhorn Arena (Thun), Tourbillon (Sion), Tuilière (Lausanne),
  Maladière (Neuchâtel), Brügglifeld (Aarau), Schützenwiese (Winterthur).
- **Arenas** (the same bowl round a floor, closed by a lid inside the roof's thickness, a walkable floor
  `FloorLift` over the pitch): Hallenstadion, PostFinance Arena, St. Jakobshalle, Swiss Life Arena,
  Vaudoise Aréna, Vernets, Geneva Arena (Palexpo), BCF Arena, Tissot Arena, Bossard Arena, Cornèr Arena,
  THE HALL (Dübendorf), SWISS Arena (Kloten), Davos.
- **Open airs** (no bowl: a 2.2 m fence round the ground, following its slope, open at its `Gates`;
  `StageAt = North` puts the main stage where the festival builds it): Paléo, Gurten, Frauenfeld,
  OpenAir St. Gallen, Greenfield, Heitere, Moon&Stars. They keep the buildings on their ground (no
  `StadiumClip`), drop parked cars and trees, and have no occluder.

## How the rows were made

- Parallel research agents (stadiums, arenas, open airs), each writing JSON under a scratch folder,
  never the repo; a second pass for the festival stages from site plans. Then reviewed: every outline
  drawn over its swissimage aerial (seating front red, facade blue) and looked at; wrong centres
  corrected (PostFinance Arena moved 7 m to its roof's middle). Measured: centre, axis, outline (2-5 m).
  Estimated: heights, tiers, colours. Open-air stages: high confidence at Paléo and Gurten (site plan
  fitted on landmarks, festival-time flights show the stage), low at Frauenfeld, St. Gallen, Greenfield.
- Rows tuned until the seats match the published capacity (`--stadiumcheck`, 12 %): a loop scaled each
  venue's rows by real/modelled seats. St. Jakob-Park and Davos hold 82 % (their depths on the aerial
  hold no more rows): a documented 20 % (`Known.Tolerance`). Terraces of standing people count at 0.42 m
  (`SeatWidth`: PostFinance Arena, Davos).
- Skipped: AIL Arena Lugano (new, the aerial is still a building site), the Pontaise (replaced),
  Halle 622 (a flat hall), Rock Oz'Arènes (Avenches, closed until 2027).

## What the builder learnt

- **Rows per segment** (`StadiumBuilder.RowsHere`): a segment holds only the rows its own depth allows
  (1.5 m kept behind), so an arena's shallow ends stop where the facade comes and the deep sides keep
  theirs; `Stand`, `SeatAt` and `Seats` agree. Tiers are fitted to the deepest side.
- A vomitory too steep to fit inside the facade is not dug (St. Jakob-Park's third tier ran out of it).
- The check is generic: each venue's own gate side (long enough, under stands tall enough to roof a
  tunnel), a seat found along the west stand, the clip test across the real facade line.

## Concerts in them

- Stages fit (`ConcertPlanner`, `StagePlan`, checked over 60 shows a venue): about 0.72 of the floor wide,
  depth 0.22 of its length, an arena's roof 4.3 m under its ceiling (canopy included); catwalk, B-stage,
  front of house, delay towers inside the floor ahead; wings, PA and side screens only where there is
  room; a video wall under a low stage roof.
- Open airs: the rig stands on the ground at the stage (`ConcertDirector`), `StagePlan.Lift` puts the
  ground-standing boxes on the terrain, the crowd follows the field (`ConcertCrowd.Ground`), at most
  24 000 on the floor. Arenas: the crowd on the raised floor.
- Beams stop at the venue's own shape (`ConcertRig.VenueReach`), not only at collision: collision is
  built only near a body, so from a hill the Hallenstadion's beams came through its roof.
- Pictures: `--chunks X:/UnitSportSwitzerland/terrain_chunks --origin <E,N> --at <E,N> --concert 13
  --time 22 --shot-queue <file>`: the local tiles do not cover Bern, Zürich or Basel.
