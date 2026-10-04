# Battle Royale zone (`src/BattleRoyale/ZoneSchedule.cs`, `ZoneWall.cs`)

- **Deterministic from (seed, side, pace, field)**: every client builds the same `ZoneSchedule` from the
  state message and reads it at `ClockSync.ServerNow - Started`. Nothing about the zone is ever sent
  while it moves. Coordinates are metres east/north of the region centre (`BrManager.ZonePoint` /
  `WorldPoint` convert).
- **Circle 0 follows the field** (#447): `BrState.Field` is the entrant count at GO (0 before: the
  lobby builds the full circle). Radius = sqrt(field x 1.5 km² / pi) (`AreaPerPlayer`), at least
  900 m (`MinRadius`), at most the full circle, 0.57 x side (the square's corners out from the start).
  5 players: 1.5 km; 10: 2.2 km; 20: 3.1 km; a crowd: full. A smaller circle's centre is uniform
  inside the full circle, clamped into the square. The region square itself is still sized at
  `/br open` for the peers online (`region` note): the plane line, loot and airdrop counts cover it.
- **Next centres**: circle i's centre is uniform in the disc of radius `r(i-1) - r(i)` around the
  previous centre, clamped into the square and pulled back inside the previous circle. So every
  circle lies inside the one before.
- **On the ground** (#477): at GO the server (`BrManager.GroundCentres`) draws each centre as above but
  tries up to `ZoneSchedule.Tries` (12) seeded candidates, keeping the first whose disc has at most
  `GoodEnough` (30 %) of bad ground, else the least bad. `ZoneSchedule.Badness`: 41 sunflower samples
  over the disc, each on water (`BrMapImage.Wet`, the horizon lattice's water level) or on a slope over
  35° (heights ±50 m) counts. The centres go out in `BrState.ZoneCentres` and every client builds its
  zone from them (`ZoneSchedule(..., centres)`), so nobody needs the lattice and nobody disagrees.
  Without the lattice (generated, fixture worlds) `ZoneCentres` is null: the seed's own zone.
  `--brcheck`: a synthetic lake + cliff, circles on bad ground 533 → 35 of 800.
- **The next circle while looting** (#477): `At` in phase 0 already gives the first shrink's circle as
  `NextCentre`/`NextRadius`; the maps draw it dashed from the start, so a rotation can be planned
  before the zone bites.
- **Timetable**: the 6 km normal baseline below; times scale by `r0 / 3,420 m x pace` (short 0.8 / normal 1 / long 1.3),
  the same as `side / 6 km` for a full circle; a small field never under half (5 players on 5 km: about 20 min a round).

  | Phase | Wait | Shrink | End radius | Damage |
  |---|---|---|---|---|
  | Loot | 4:00 | - | 3,420 m | 0 |
  | 1 | 2:00 | 3:00 | 0.618 r0 | 1 HP/s |
  | 2 | 3:00 | 2:30 | 0.397 r0 | 2 HP/s |
  | 3 | 2:30 | 2:00 | 0.25 r0 | 3 HP/s |
  | 4 | 2:00 | 1:45 | 0.147 r0 | 5 HP/s |
  | 5 | 1:45 | 1:30 | 0.082 r0 | 7 HP/s |
  | 6 | 1:30 | 1:15 | 0.041 r0 | 10 HP/s |
  | 7 | 1:15 | 1:00 | 0.0176 r0 | 15 HP/s |
  | 8 | 1:00 | 1:00 | 0 | 25 HP/s |

  That makes 33 min at 6 km normal, plus about 2 min of countdown and landing. Damage is per second
  outside the *current* circle, from the start of each phase's wait.
- **Damage**: on the owner's machine (health is the owner's). Every 0.5 s,
  `TakeDamage(dps x dt, 0, DamageCause.Zone)`. The check is horizontal only: an interior lies straight
  under its building, so indoors needs no special case.
- **Wall** (`ZoneWall`): an open `CylinderMesh` scaled to the radius, 3 km tall around the camera's
  height. Drawn by `shaders/br_zone_wall.gdshader`: unshaded, cull off, stripes about every 12 m,
  rising bands, stronger up close, Bayer-dithered. Hidden at radius 0.
- Not yet: glaciers (the lattice has no cover classes); a terrain tint outside the circle (shader globals).
