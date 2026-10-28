# Cinemas (#1238, epic #1232)

The generator for OSM's `amenity=cinema` solids (`entertainment-venues`), and the projection booth
that works their lights and screen. Research numbers: `venue-cinema`. Built on the bowling centre's
lane frame and rules (`bowling-centres`).

- **Branch**: as bowling, `TryCinema` before the industrial and flats rules; two levels when the
  height allows (a whole building from 7 m of wall), then one, keeping only a plan the validator
  passes, else the layout back untouched. `CinemaRules` re-plans cinemas only (bump it to see a
  change in a building you have stood in: its plan is stored).
- **Along v** (from the door's end): the lobby, 25 % of the length, 4.6-12 m (along X it reaches past
  the door); the auditoriums, 8.5-32 m; the booth, 2.6 m; past that a plain `Hall`.
- **Across u**: the staff corridor (1.6 m) on the door's side, then the halls side by side at about
  13 m each (7-18 m, up to 10: a multiplex); a column of public toilets (by gender in a deep lobby, else a
  shared anteroom, a narrow cinema one WC: `public-toilets`, #1285) and a `Storage` at the lobby's end
  away from the corridor; a mall's remainder a plain `Hall`.
- **The screen faces the lobby**: an auditorium's screen is on its lobby wall and its seats rise
  away from it, so the door from the lobby is beside the screen at floor level and you climb the aisle
  steps, as in many real halls. The booth is behind the top rows: upstairs on two levels (a straight
  stair up the corridor to a landing, the booth's door off it, a store under the booth), at floor
  level on one.
- **Auditorium** (`Auditorium`, laid out by `InteriorGenerator.Cinema`): the `MovieScreen` (stage,
  masking, the picture at 2.39:1 with its bottom 0.9 m up, curtains), clear of the door's corner (the
  width less 1.8 m each side); the `SeatingBank` from 0.6 screen widths (at least 3 m) to 0.7 m off the
  back wall, and 0.7 m off the side walls: #498 puts extra doors round long buildings, and in a hall
  they are its fire exits, whose doorway strips must stay clear. Rows 1.1 m apart, the rise set by the
  headroom (at most 0.32 m), seats 0.58 m with backs and armrests, an aisle each side with half steps
  and step lights, and a ramp of collision up each aisle (the steps are walked, not climbed). Exit
  signs over the doors, the `Porthole` high in the back wall (a piece, not a cut: a real opening
  between booth and hall would be glass the shader draws as sky).
- **Lobby** (`CinemaLobby`): the box office by the door, the concession stand away from it (popcorn
  machine glowing, soda fountain, candy case, lit menu boards), then by area: posters, standees,
  stanchions, benches, plants, and from 200 m2 a few arcade cabinets. Carpet print as bowling's.
- **Booth** (`ProjectionRoom`): a projector to each hall, the rack, an old 35 mm platter, a chair, and
  the `BoothPanel`. **`CinemaBooths`** (`World/CinemaBooths`, server and clients, protocol 52) owns each
  cinema's state (plan key -> lights Full/Dim/Off + screen on/off, packed in three bits): a peer asks
  (`AskSet`), the server serves it only if `SpaceOf(peer)` is that plan, broadcasts `Set`, and sends the
  table to a joiner (`ServerWorld` beside the church radios). E (pad interact, VR grip via
  `TryInteract(byHand)`) at the panel's left half cycles the lights, at its right half switches the
  screen; the prompt says what it will do. Bots: `Serve(peer, plan, code)` on the server.
  From the chat (#1306, an admin's): `/cinema <hall 1..> [lights full|dim|off] [film on|off]` in the cinema the sender
  is in, or offline the one the free camera is in (`CinemaBooths.Command`; the shot queue's films set halls with it).
- **Showing it** (client, on change only): the interior material's `house_lights` (1, 0.3, 0) scales
  the tint-2 lamps (`RoomLights.Tint`); the cat (`InteriorNode.CatPicture`: a cartoon cat drawn by code,
  no image file) on every screen, 2 cm proud of it, and the projector's beam from each hall's porthole
  (`projector_beam.gdshader`, additive), built hidden with the interior. Per hall since #1279 (below). Dim was 0.12 first: at night
  that left a hall black.
- **Outside**: as bowling (blind box, double door, the name and CINEMA big over the door).
- **Staff**: a cashier at the box office, a seller at the concession stand (`InteriorNode.Staff`).
- **Hall height** (#1279): in a building of storeys the halls span as many as give them 6 m clear
  (`HallMinClear`), two or three (`MaxHallStoreys`), the booth one storey up: Geneva's Grütli (28 m of
  2.76 m storeys) had one-storey halls 2.56 m clear before. A building with no storey grid (`Storeys` count
  under 2, Cinérama Empire: 32 m tagged a garage) takes a whole hall's heights. Every real cinema on the
  Geneva/Vaud tiles now has its booth upstairs; the deck is for low halls (under 7 m) only.
- **A panel per hall** (#1279): each hall's `BoothPanel` stands on the booth's hall-side wall beside that
  hall's viewing windows; `CinemaBooths` keeps lights and screen per hall (`HallAt`: the auditorium whose middle
  is nearest, numbered as the ground floor lists them; protocol 58 carries the hall). Each screen and beam
  shows its own hall's switch; `house_lights` and the screen's wash are one value for the whole interior, so
  they show the hall the camera is in or nearest (`InteriorNode.HallNear`).
- **A way to the panels** (#1279): the walk grid (`InteriorGenerator.Walkable`) also covers a strip room up to
  4 m wide and 400 m2, whatever its length (the booth is 2.6 m by up to 118 m), and a strip or a projection
  room is walked at a player's width (`BodyRadius` 0.34 m, not the rooms' 0.24 m): a platter left two 0.6 m
  lanes the grid passed and the body did not. Each panel's use spot is kept clear, and `--cinemacheck` walks
  to every panel (`CanReach`, which grids any room).
- **Seats, stairs to the walls, a service door** (#1279): the bank runs wall to wall (its aisles against the side
  walls, `AisleWidth`; off a wall, `ExitClear`, only where a facade doorway opens beside the seats, which no cinema
  has now: a cinema keeps its one entrance, `BuildingFootprint`'s budget 1) and its rows to the back wall (the top
  row against it). Rows (`InteriorGenerator.RowsOf`, one source for the mesh and sitting): the seats folded at each
  row's back (`SeatDepth`, solid), a lane of 0.8 m in front walked to them, every other row one seat fewer and
  staggered half a seat. `CinemaSeats.At`: from a row's lane, E sits on the seat in front (`FootPlayer.SitInReach`,
  the stands' sitting, #885: nothing new on the wire), facing the screen. At the top of the aisle away from the
  booth's door, `ServiceSteps` (up to 4, `ServiceTread` 0.25 m) climb from the top aisle's lower half step to a
  service doorway into the booth (`CinemaPlan.Service`): an open `Window` between the floors (doors are
  same-floor only) with a real leaf, an `InnerDoorPlan` with a sill (`Bottom`), on the booth's floor or deck. Moved
  to the other aisle when a facade doorway keeps the seats off its wall, dropped when the rise is more than the
  flight (six rows under a 3.5 m booth). The wall speakers and lights ride up the aisle.
- **Halls mid-film, doors shut** (#1279): `CinemaBooths.Default(plan, hall)`: 40 % of halls (`ShowingShare`, from
  the hall's seed) start lights off with the screen on, the rest lit and dark; every peer starts the same and only
  what players set is sent (`SendTo` skips a hall at its default). Every hall's door from the lobby is an
  `InnerDoorPlan`, a leaf shut until someone opens it.
- **Bump `CinemaRules` with every change to a cinema's plan**: stored plans are kept by it, and a game that
  saw a cinema keeps its old plan otherwise (#1279's panels, aisles and service doors did not show until 11).
- **The beam** (#1279, `projector_beam.gdshader`), after research (froxel fog ignores the projector texture and
  ghosts, a spatial shader cannot read the shadow map): the view ray clipped to the pyramid (Cyrus-Beck), 24
  equiangular samples round the lens (light per metre 1/d^2: bright at the projector, nothing at the screen), a
  Henyey-Greenstein phase g 0.6 with a 20 % isotropic floor (bright looking back at the lens, faint from the side),
  and up to eight capsules (`InteriorNode.BeamBodies`: players, the viewer's walker) shadowing it, written per frame
  only as they move. Its lines meet 0.2 m behind the lens's glass (`ApexBehindLens`). The plain beam looks the same from afar
  (#1298): ten rings along its cone, vertex alpha `BeamFade` falling with the square of the distance past the first
  sixteenth of the throw, nothing at the screen. The spot's own shadow still does the screen.
- **Cushions** (#1279): their own mesh per seating bank in the interior's frame (`InteriorMeshBuilder.Cushions`, the
  room lights are looked up by it), folded, or let down under a body in the sit pose (`InteriorNode.UpdateSeats`,
  per frame from `CinemaBooths` without allocating, the mesh built again only when a bank's seated set changes).
  The bank keeps their collision. The projectionist's chair stands between the first projector and its panel,
  facing the hall's window. While the screen is on, the port shows the picture faintly where glass would be
  (`PortPicture`): the cone's own cut through the wall's line (the screen's picture scaled by apex-to-wall over
  apex-to-screen, on the beam's axis, mirrored: seen from the light's side), the wall hiding all but the hole. The
  lens's glass is dark; beside it a 0.18 m bloom of the picture's mean colour brightened (`LensGlow`, a billboard).
- **Smooth house lights** (#1279): a hall's lights fade to their new level over 6 s (`InteriorNode.FadeHouse`,
  `HouseFadeSeconds`, the shader value written only while it moves); walking into another hall, or into the
  cinema, shows its level at once.
- **House lights off is dark** (#1279): in a room lit by tint-2 lamps the ambient (`world_tint * ambient_day`)
  scales with `house_lights` too (floor 3 %), so only the screen's wash lights a hall with its lights off.
- **New music each showing**: bits 3-6 of a hall's code count its showings (`CinemaBooths.ReelOf`), the server
  counting up as the screen comes on; the hall's music draws again from it.
- **Box office glass** (#1279): a see-through pane of its own (`InteriorNode.AddClearGlass`), the mesh only its
  rail and posts: the interior's one mesh is opaque, and vertex alpha marks lit faces or sky glass.
- **Checks**: `--cinemacheck --systems ui` (`CinemaProbe`: a two-level town cinema, a small one, a
  4-hall multiplex, one under flats, one along X, one too small); `tools/cinemanetcheck.sh` (net: A
  inside a generated house is granted lights off and the cat on, B outside sees it and is refused, C
  joining late gets it from the table: the booth's rule is "only from inside", whatever the building).
  `--cinemashow` dims every cinema with the cat on, for shots. Model viewer: every piece, and two whole
  cinemas with the show on: `--modelsonly "Cinema - Town" --modelsyaw 0 --modelslook 0.8,3.3,-7.8,9`
  is the screen from the seats (wider views from further back came out black: not chased).
- **Real**: the venue check computes doors with the tile's roads now (`--venuecheck`): without them a
  door can land on another facade, and a shot aimed at it enters the neighbour. CinéVersoix (60 x 69 m,
  13 m, halls along X): the foyer `703.45,i1.6,-103.8,-4,163.2,6,<png>` from origin 2501000,1126000.
- **Video** (#1298): the panel's screen half opens the hall's projector panel: a link played for the hall on the shared clock (`cinema-video`).
