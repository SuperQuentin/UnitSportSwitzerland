# Restaurants, cafés, bars and clubs (#1242, epic #1232)

What is inside the ~28,000 dining venues OSM knows (`entertainment-venues`). Research numbers:
`venue-dining`. Terraces: #1243; music: #1244.

- **No planner of their own.** The building's own planner makes its rooms as before; just before
  furnishing, `DiningUnits` hands each dining venue the solid holds (`BuildingTypeMap.DiningOf`, all of
  them, not only the ranked winner) a ground-floor room: the best kind first (shop, living room, dining
  room, office, classroom; a hall last, it is a house's stair hall), then the nearest the venue's point,
  then the biggest. One room per venue; a venue with none left, or none big enough (9 m2 for a café or
  a kebab, 14 m2 else), is not drawn. The kitchen, store or pantry off it becomes its `GastroKitchen`; a
  house's kitchen across the hall all the same; a house's dining room off it a second guest room (the
  Säli). Flats above stay flats.
- **Blocks of flats**: only shop units are taken (a flat's living room is someone's). A block holding a
  dining venue plans its ground floor as shop units, as a mixed block (`DiningShops`, set per thread
  round `TryApartments`; the garage ramp plan is the same either way). Hall venues (bowling, cinema,
  arcade) keep their plans; a restaurant in one is not drawn. A building holding a venue is no bank
  (the dice that make banks up skip it), and a shop building whose shops all became restaurants loses
  its promised shop counter.
- **Kinds** (`DiningRoomOf`, from the type, then the cuisine and the name: many pizzerias are tagged
  only by their name): `Gaststube` (restaurant), `Pizzeria`, `CafeRoom`, `FastFoodRoom`, `KebabRoom`,
  `BarRoom`, `PubRoom` (pubs and beer gardens), `ClubRoom`. Each its palette (a Beiz's dark parquet and
  wood ceiling, terracotta, pale tiles, oxblood, black) and its furnishing (`DiningLayout`):
  - the bar by the kitchen's door (the Buffet's swing door), else facing the way in; the way in kept
    clear 2 m from the street door, 1.2 m from an inner one;
  - Gaststube: Buffet, a tiled stove (35 %), the Stammtisch (round, eight chairs), banquettes, tables;
    Pizzeria: the oven, a short bar, wine rack; Café: the pastry case by the door, tables for two; fast
    food and kebab: the order counter (the spit on a kebab's), booths; bar and pub: the long bar with
    stools, a stage (from 60 m2), high tables; club: the lit dance floor in the middle, the DJ booth
    facing the bar, sofas;
  - games by the research odds: darts, pinball and jukebox at the walls before the tables, table
    football and pool in the floor the tables leave;
  - tables in a grid tried centred and shifted half a pitch each way, laid where it seats most (a
    centred row fell on a bar's stools), each where it leaves a way through (`KeepsWay`).
- **Pieces** (`InteriorMeshBuilder.Dining`): a piece's woods, cloths and pads follow the room it stands
  in (`_room`, set per piece in `Build`): white cloths in a Beiz, red checks in a pizzeria, marble in a
  café. Counters are counter-high in the plan (the menu boards hang over the back line at their own
  height), so a shopfront's windows do not keep them off its walls; the cook line falls back to shorter
  lengths on inner walls.
- **Toilets** (#1285, `public-toilets`), each venue's, in this order: at the end of the kitchen strip
  walled off its room (the end with no doorway; a set the strip's length past a 2.4 m kitchen holds);
  else a strip walled off a wall of its room with no doorway in or near it, as deep as its set needs,
  the room keeping 4 m across and most of its floor (a Beiz its tables); else, for a bar or a club,
  the store off its room (the set's first room takes the store's slot); else a WC the building
  already has on its ground floor is adopted (a leaf, the sign). `InteriorLayout.CurrentVersion` 38.
- **Staff**: bartender, cashier, the pastry case's server, the DJ (`InteriorNode.Staff`).
- **Light**: a club's lamps tint 3 (violet), as an arcade's.
- **Checks**: `--diningcheck --systems ui` (`DiningProbe`: a Beiz house, a café, a pizzeria, a bar, a
  club, a kebab, a block of flats with a restaurant and a bar, a kiosk too small; SVG plans in
  `test_output/dining/`). Model viewer: every piece, and `Interiors / Dining - <kind>`; a close look
  inside the venue's room: `--modelsonly "Dining - Pizzeria" --modelsyaw 20 --modelspitch -25
  --modelslook -3.5,0.8,-4.2,3.0` (a negative pitch looks down). Their z-fight lines are the
  commercial building's doorway seams and the staff's faces.
- **Real**: `--venuecheck --chunks <dir> --venuedoors <name>` prints the door of every venue whose name
  holds it, and its rooms in LV95. A shot's camera inside stands where it is told, but a point inside a
  block finds a neighbour's door first and lands in that interior: aim from just outside the venue's
  own door.
- **Next**: terraces (#1243), music and chatter (#1244), signs for the venues a building holds besides
  its winner.
