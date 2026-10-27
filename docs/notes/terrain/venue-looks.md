# Venue looks: fronts, signs, fuller rooms (#1250, epic #1232)

The owner's review of the venues, decided with them. References (Cine Rex Bern, Plaza Zürich,
Cineplexx Innsbruck, Bluff Billard Geneva, foyers of Cineplexx, Kino Svetozor, CinemaxX) were looked
at, not shipped.

- **Signs by kind** (`VenueSigns.Dress`): neon for bowling, cinemas, arcades, bars, clubs
  (`VenueSigns.Neon`); a painted wooden board (`PlankSign`: five paints, a frame, iron brackets, two
  lamps lit at night, the name in Saira) for restaurants, cafés, pubs, fast food, beer gardens; every
  café and the restaurants whose dice say so (`VenueTerraces.HasAwning`) have their name on the
  awning's valance instead, with an awning alone over the door when no terrace fits.
- **Whole halls up to 20 m** (`Venues.HallMaxHeight`, was 13): a bowling centre, cinema or arcade
  that is its building has no window grid; a taller one loses only its ground storey's windows
  (`BuildingMeshBuilder.BlindGroundFlag`, UV2.y 0.25, read by `building.gdshaderinc`). Its main door
  (`DoorSpot.WholeHall`, `Top`, `Run`) gets `HallFront`: a glazed front of 1.1 m bays round the door,
  a canopy with downlights, at a cinema the film titles on the fascia and lit poster cases (made-up
  films, posters drawn by code), the name over the canopy and the big word half a metre under the roof
  (`VenueSigns.BigY`). Model viewer: `Interiors / Hall front`, `Interiors / Plank sign`.
- **Cinemas**: a door far along a long wall turns the plan (halls across, the lobby along the door's
  wall: CinéVersoix's lobby was 36 x 57 m); the foyer's middle gets columns every 8 m and groups on
  every other cell (`InteriorGenerator.Fill`: high tables, sofas, two pairs of standees, tables); the
  screen shows six CC0 cat photos (`assets/cinema/`, `ASSETS.md`), one every 8 s on the shared clock
  (`CinemaBooths`, `InteriorNode.SlideAt`); the hall has its screen speaker stacks, surround speakers
  and wall lights (`Loudspeaker`, `WallSconce`); the booth has its own face of each porthole; the
  beam samples the picture along each view ray (`projector_beam.gdshader`) and the house lamps carry a
  wash of the slide's mean colour (`screen_glow`).
- **Arcades**: a snack bar and game tables in what the islands leave. One whose name says billard,
  billiard, snooker or pool is a `BilliardHall` (Bluff Billard is tagged an amusement arcade): at most
  22 m a side, up to 12 pool tables in bays with the cue's swing clear, the bar, lounges, cue racks.
- **Dining**: a venue that cooks and has 24 m2 or more gets a kitchen walled off the back of its room
  when none is next door (`BackOfHouse`, `Carve`), and a WC at the strip's end; a room over 85 m2 is
  cut: the first part a Säli through an arch (no bar of its own), what is still too big becomes
  stores, rooms kept near square. Tables vary (fours, twos either way, sixes, a hand off the grid).
- **Shots**: indoors the shot camera's near plane is 5 cm; an "i" shot's door is looked for at a
  walker's height within 50 m, so a camera high over an auditorium's back rows or on a booth upstairs
  works. `--venuecheck --chunks <dir> --venuedoors <name>` prints every room with its furniture and
  writes the plan to `test_output/venues/<tile>_<index>.svg`.
- **Projection POC** (`CinemaProjectionPoc`, model viewer `Interiors / Cinema projection (POC)`): the
  picture as a shadowed spot light's projector texture, so a figure in the beam wears it and shadows
  the screen. `--projectionperf` (windowed) measures its cost in the Town cinema. Not in the game.
