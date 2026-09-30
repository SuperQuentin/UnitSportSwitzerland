# Racing line: the safe verge

- **Racing line: the safe verge** (`RaceLine.Widen`, #39). The line may put up to half the car (its
  centre on the tarmac edge, two wheels off) onto the verge where a survey says it is safe, per
  centreline point and side, from data every client has: the full-res height grid (the ground within a
  car width, 1.8 m, past the edge must not fall more than 0.5 m — else **margin 0**; a bank rising past
  ~30% stops it; the road more than 0.6 m off the ground at its edge — bridge, embankment, cutting —
  is margin 0), trunks from `.trees` (same radius as `TreeColliders`, 0.5 m clearance, within a car
  length along), walls, railways and watercourses from `.road`, water/wetland/glacier cover, building
  footprints. Each side is only as good as its worst neighbour within ±2 points (a car length). Any
  missing data is margin 0. Beside a drop the line keeps a further 0.9 m inside the tarmac (#52; 0.3 then 0.5
  still let a pack's tracking error put wheels over), and the pilot's `EdgeGuard` holds the body off
  any blocked edge (`racecraft`). The speed profile takes a quarter of the grip off where the line runs on
  the verge. The survey runs off the main thread when a pilot is made (or before GO in `--drivecheck`)
  and swaps `route.Line` for the widened one (same point count, so indices stay valid).
  Mollendruz (8.4 km): 12.0 of 16.7 km of edge usable, blocked by Drop 4.3 km, Bank 0.3 km; the line
  uses it over ~490 m. **Trees never block it here: the preprocessor keeps trunks ≥ 2.5 m off a road
  edge**, beyond the 1.5 m counted. `AutoPilot.VergeMetres` / `VergeUnsafe` count metres driven with a
  wheel off the tarmac where the survey allowed it / did not (by reason); `--drivecheck` prints both.
- Overtaking uses the same room: a pass is taken only where the rival's offset plus 2.0 m fits inside
  tarmac + safe verge on that side, the line's own 0.3 m off the edge given up only where the verge
  beyond is safe (never at a blocked edge), never with oncoming traffic or something stopped ahead;
  on straights, or as a dive up the inside (`racecraft`).
