# Venue terraces (#1243, epic #1232)

Tables out in front of restaurants, cafés and bars (`VenueTerraces`). Research numbers:
`venue-dining` ("Terraces and outdoor dining"); the venues: `entertainment-venues`, `dining`.

- **Which**: the venue a door carries (`DoorSpot.Venue`, a building's ranked winner), a dining kind:
  as OSM says when it says (`outdoor_seating`, `VenueFlags.Outdoor`/`NoOutdoor`), else by odds of its
  OSM id [E]: 0.55 restaurants, cafés and bars, 0.5 pubs, 0.35 fast food, 0.9 beer gardens, a club
  never.
- **Where** (`Plan`, pure, tile-local): sets on both sides of the way in, from 0.9 m past the door's
  jamb, 1.6 m each (a beer garden's benches 2.2 m, in two rows, up to 3.6 m out), two a side (three).
  A set is laid only where (a) its outer edge keeps `Passage` 1.5 m from the carriageway [S: Basel]
  (`Carriageway`: drivable classes and rails, not footpaths or squares, where terraces belong; the
  sidewalk is part of a segment's side, so the distance to its carriageway edge is the pavement),
  (b) its own ground is level within 0.15 m, and (c) that ground lies within 0.6 m of the door's
  sill (a door often stands a step or two up: 0.3 m kept Diabolo Pizza's terrace off its pavement).
  The row stops at the first set that does not fit. Each piece stands on its own ground.
- **What**: bistro sets (marble top, two rattan chairs), parasols through the tables or, for half the
  cafés and restaurants, an awning across the terrace (stretched to its width, its valance at 2.0 m),
  planters closing the row, a chalk menu board by the door; beer benches for a beer garden. One colour
  per venue for parasols and awnings [S: Basel]. `TerraceProps` are `PropKind`s, so the model viewer
  shows them under Occasions / Prop.
- **When**: built on `ChunkManager.TileFurnished` once the tile's roads are loaded
  (`IChunkSource.LoadRoadsAsync`, as `OccasionDecor`), parented to the tile's node. Client only and a
  pure function of the tile: every client lays the same.
- **Solid**: one `StaticBody3D` per tile on the trees' layer (`World.TreeColliders.Layer`): walkers
  and cars stop at tables, planters and boards, the chase camera is not pushed in; parasols and
  awnings overhead do not collide. Like trees, server-run bots do not see them.
- **`MeshScratch` turns its meshes half round** (`(−x, y, −z)`, a figure's convention): a prop authored
  with +Z to the street is placed turned back by π, or the awning goes into the wall and the board
  shows its back. Its collision box, in plain Godot space, takes no turn.
- **Checks**: `--terracecheck` (`TerraceProbe`: the pure plan in front of a hand-made door: a pavement
  6 m deep, one 2.5 m deep, OSM's no, a club, a level 1 m up on one side, a 1-in-4 slope, a door a
  step up, a beer garden; the pieces build). `--terracelog` prints, in a running game, each wanted
  terrace's count, the carriageway distance and the ground. Lausanne (`--origin 2537000,1151000`,
  real tiles): 214 of 303 wanted terraces drawn; Diabolo Pizza `624.55,g1.7,-904.12,-4,71.4,6,<png>`.
- **Not here**: seasons (stacked away in winter), string lights at night, terraces on squares deeper
  than a pavement, venues a building holds besides its winner.
