# Public toilets by gender (#1285)

Every publicly accessible building gets toilets sized to the space it has: cinemas, bowling centres,
arcades, restaurants, cafés, bars, IKEA stores. One planner (`InteriorGenerator.Toilets`) lays a set into a
strip of the building a generator hands it; the sizes and counts are pure rules (`ToiletSizing`, unit
tested).

> Research, Oct 2026: tagged [S] sourced, [E] estimated, [D] derived. Constants built from [E] numbers stay tunable.

## Layouts and dimensions

- **Cubicle (stall)**: 0.8 m wide x 1.5 m deep inside is the usual recommendation, 0.85 m an ambulant one,
  1.2 m an enlarged one [S: Commercial Washrooms, DuraCube]. Doors that swing in are 0.58-0.66 m [S: Bobrick];
  0.6 m x 1.72 m door [S: Jialifu]. Partitions 1.95-2.4 m tall [S: Jialifu]; floor-to-ceiling cabins
  ("raumhohe WC-Trennwände") are common in Swiss public buildings [E].
  Here: a stall is a room 1.0-1.4 m wide (0.88 m clear between the 6 cm wall insets) and **1.6 m** deep,
  walled to the ceiling, its **0.7 m door** swinging in (the player's capsule is 0.64 m across).
- **Urinals**: centres 600 mm at least with screens, 700-750 mm comfortable [S: Peacebuild]; 30 in (0.76 m)
  [S: Cubiq]. Privacy screens about 400 mm deep [S: TPI], starting at most 12 in (0.3 m) off the floor and
  rising to at least 60 in (1.52 m) [S: IPC 2021 405.3.5]. Here: **0.7 m** pitch, a 0.45 m screen between
  each pair and at both ends of the row, 0.3-1.5 m.
- **Basins**: one per one to two cubicles or urinals [E]; every 0.6 m [S: venue-dining]. Here **0.65 m** a
  basin, in one counter (`SinkRow`) with a mirror over each basin (`Mirror`, so each is a `WallMirror`) and a
  hand dryer.
- **Entrance lobby ("maze")**: the main entrance preferably has no door, and cubicles, urinals and mirrors
  are kept out of the line of sight from it; an offset maze entrance blocks the view and is hands-free
  [S: Restroom Association Singapore, Guide to Better Public Toilet 6th ed. 2025]. High-volume rooms
  enter through a maze that prevents a direct line of sight from the corridor, at least 5 ft (1.5 m)
  clear [S: Maryland Aviation Administration, restroom standards 7.6]. Swiss workplaces: toilets
  separated from work rooms by a ventilated anteroom (Vorraum), hand washing near them, by gender as
  a rule [S: ArGV 3 Art. 32, SECO Wegleitung, via arbeitsrecht-aktuell.ch].
  Here: each gendered room is entered through its own **anteroom 1.6 m wide** (`ToiletLobby`), the
  basins along its outer wall; the doorway into the washroom is on the anteroom's side wall at its
  far end, square to the doorway from the corridor, so no straight line runs from the corridor
  through both into the room. The doorways are open holes (no leaf to push, as the guide prefers).
- **Accessible WC** (SIA 500): at least **1.80 x 1.65 m** with the door opening out, **1.65 x 2.30 m** with
  it opening in; the bowl's axis **0.45 m** from the corner; usable door at least 0.80 m; the figures are
  binding, mirrored allowed [S: hindernisfreie-architektur.ch, Procap M105, SIA 500-C2]. Here: 1.8 m
  wide, at least 2.3 m deep, the door (0.9 m) swinging in, the bowl 0.45 m off the side wall, a
  fold-down rail on its open side and a rail on the wall, a basin within reach.
- **Counts** (no federal rule): Fribourg one WC per sex from ~30 seats, an accessible one from ~11;
  Zurich separate genders from 50 seats; Geberit for 100 seats ~5 women's, 3 men's + 4 urinals
  [S: venue-dining]; cinema 1 cabin per 40-60 seats + 1 accessible [S: venue-cinema]. Here [E], from
  the people the host room holds (`ToiletSizing.Fixtures`): women's stalls `p/25`, men's stalls
  `p/80`, urinals `p/40`, each at least one (women two), capped by the space; a gendered set from 30
  people, an accessible WC from 60.

## The three sets (`ToiletSet`, chosen by the strip's size, `ToiletSizing.Choose`)

Frame: a strip along one wall of its host room (the lobby, concourse, hall, guest room), `a` along the
host wall, `b` away from it.

- **Gendered** (plenty of space; strip at least ~9 m long and 2.8 m deep): along `a` the women's anteroom
  and washroom, the men's anteroom and washroom, then the accessible WC where the building is big.
  Women's washroom: a row of stalls along its far side wall, doors into the aisle. Men's: the stalls
  likewise, the urinals along the host wall with their screens. Signs (`WcSignWomen`, `WcSignMen`,
  `WcSignAccessible`) over each doorway on the host's side.
- **Shared** (less space; ~2.4 x 2.9 m): one anteroom off the host with the basin(s), two stalls off its
  back wall, a women's and a men's sign over their doors; a `WcSignAll` over the way in.
- **Single** (really no space; ~1.4 x 1.9 m): one `WC` room, bowl, basin, its own door, `WcSignAll`.

## Where each building has them

`PublicToilets(l, floor, host, strip, hostSide, startNear, people, clear, reuse)` plans into a strip; the
rest of the strip comes back (at least `ToiletRest`, 1.2 m: a shorter sliver widens the set's last room)
for the caller's store. Bump a generator's rules constant when its toilets change.

- **Cinema** (`CinemaRules` 10): the column beside the lobby, 3.4 m wide in a lobby 9 m deep or more
  (gendered), 3 m (shared), 2 m in a cinema under 10 m wide (one WC); seats ~0.6 a m² of hall.
- **Bowling** (`BowlingRules` 5): the concourse's service column the same way; six people a lane, twice.
- **Arcade** (`ArcadeRules` 6): the 3 m strip across the back, from the end away from the door; a
  store kept at the door's end when a mall's back hall is reached through it; a player per 4 m².
- **Dining** (`InteriorLayout.CurrentVersion` 38): `dining` (the kitchen strip's end, a strip off the
  room, a bar's store, or the building's own WC adopted with `AdoptWC`); seats 1 a 1.4 m².
- **IKEA** (`IkeaRules` 2): a low block along the back wall, `ikea`.
- **Not yet**: shops, offices and civic buildings planned by the cored rules keep their plain `WC`
  rooms; stations and church halls have no planner of their own.

## Checks

- `ToiletSizingTests` (tier 0): the sets and counts.
- `ToiletCheck.Problems` (each generator's probe: `--cinemacheck`, `--bowlingcheck`, `--arcadecheck`,
  `--diningcheck`, `--ikeacheck`, all `--systems ui`): the set the case's footprint should have, and a
  whole one: basins and mirrors in each anteroom, its two doorways on square walls, a bowl and a leaf in
  every stall and accessible WC, as many screens as urinals and one, the rails, the signs, every
  doorway of a toilet room reachable among its pieces. Each generator has a single, a shared and a
  gendered case.
- Model viewer: `Interiors / Public toilets - Gendered|Shared|Single`, and every piece under Furniture.
  Inside: `--modelsonly "Public toilets - Gendered" --modelsyaw 180 --modelspitch -5 --modelslook 0,1.4,0.6,4.2`
  (the doorways from the hall), `--modelsyaw 0 --modelspitch -12 --modelslook 0.6,0.9,0.9,2.5` (the urinals).

## Doors

- Stall doors, the accessible WC's and the single WC's are real leaves (`InnerDoorPlan`, as a flat's
  front door, #557): server state, open or shut for everyone. The leaf hangs in the outer room and swings
  into the toilet (`Room` is the outer room). Not lockable.
- **Keyboard / pad / VR**: the flat doors' existing action, nothing new: E / Y at the door opens or shuts it,
  in VR grip it (`InteriorManager.InnerDoors`, `xr/vr-action-map` "flat door"). `InputHints` prompts
  "Open the door" / "Close the door".
- **Bots**: the server path is `ServeInnerDoor` (a bot's peer at the door); a bot walks through an open
  doorway by `MoveTo`. Same gap as every interior door (`bots/bot-capability-map`): no interior collision on
  the server yet.
- Anteroom doorways are open holes: the maze replaces the door.

## Light and perf

- A stall has no lamp of its own: the washroom's (or anteroom's) lamp reaches it (`RoomLights`, its box
  grown over the stalls that open off it), as a real ceiling light does over cubicle tops. So a set adds
  two or three lights, not one per stall.
- Mirrors are framed `Mirror` pieces: only the nearest one renders (`wall-mirrors`).

## Sources

- https://www.commercialwashroomsltd.co.uk/blog/what-are-the-standard-toilet-cubicle-sizes.html
- https://architectureanddesign.com.au/suppliers/duracube/guide-to-code-compliant-toilet-cubicle-dimensions
- https://www.bobrick.com/wp-content/uploads/TPMG-297.pdf
- https://www.jialifucn.com/do-you-about-the-toilet-cubicle-sizes/
- https://peacebuildcon.com/sanitaryware/urinal-size
- https://www.cubiq.in/complete-guide-on-standard-dimensions-for-public-toilets/
- https://tpi.net.au/products/urinal-privacy-screens/urinal-privacy-screen-wall-mounted/
- https://codes.iccsafe.org/s/IPC2021P1/chapter-4-fixtures-faucets-and-fixture-fittings/IPC2021P1-Ch04-Sec405.3.5
- https://www.toilet.org.sg/docs/Guide Better Public Toilet _ 6th Edition2025.pdf
- https://public.airportal.maa.maryland.gov/PEGS/Volume_2_-_Architectural_and_Engineering/Chapter_07_Architectural_Buildings/7_6_Restroom_Standards.htm
- https://hindernisfreie-architektur.ch/fachinformation/planung-rollstuhlgerechter-toiletten/
- https://www.procap.ch/fileadmin/files/procap/Angebote/Beratung_Information/Bauen_und_Verkehr/Downloads/Merkblaetter_und_Checklisten/Oeffentlich_zugaengliche_Bauten/Rollstuhlgaengige_Toiletten/M105_Rollstuhlgerechte_Toiletten_in_oeffentlich_zugaenglichen_Bauten_08_2019.pdf
- https://shop.sia.ch/1b9389af-2d73-4cd0-ba2f-502bae7b69bd/D/DownloadAnhang (SIA 500-C2)
- https://www.seco.admin.ch/de/wegleitung-argv-3 , https://www.arbeitsrecht-aktuell.ch/de/?p=4440
