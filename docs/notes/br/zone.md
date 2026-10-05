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
- **Timetable**: the 6 km normal baseline below; times scale by `r0 / 3,420 m x pace` (short 0.8 / normal 1 / long 1.3),
  the same as `side / 6 km` for a full circle; a small field never under half (5 players on 5 km: about 20 min a round).

  | Phase | Wait | Shrink | End radius | Damage |
  |---|---|---|---|---|
  | Loot | 4:00 | - | 3,420 m | 0 |
  | 1 | 2:00 | 3:00 | 0.618 r0 | 2 HP/s |
  | 2 | 3:00 | 2:30 | 0.397 r0 | 3 HP/s |
  | 3 | 2:30 | 2:00 | 0.25 r0 | 5 HP/s |
  | 4 | 2:00 | 1:45 | 0.147 r0 | 7 HP/s |
  | 5 | 1:45 | 1:30 | 0.082 r0 | 10 HP/s |
  | 6 | 1:30 | 1:15 | 0.041 r0 | 14 HP/s |
  | 7 | 1:15 | 1:00 | 0.0176 r0 | 20 HP/s |
  | 8 | 1:00 | 1:00 | 0 | 30 HP/s |

  That makes 33 min at 6 km normal, plus about 2 min of countdown and landing. Damage is per second
  outside the *current* circle, from the start of each phase's wait. Raised in #455 (was 1, 2, 3, 5,
  7, 10, 15, 25): 50 s outside the first circle now kills, where 100 s of phase 1 used to be shrugged off.
- **Damage**: on the owner's machine (health is the owner's). Every 0.5 s,
  `TakeDamage(dps x dt, 0, DamageCause.Zone)`. The check is horizontal only: an interior lies straight
  under its building, so indoors needs no special case.
- **Wall** (`ZoneWall`): an open `CylinderMesh` scaled to the radius, 3 km tall around the camera's
  height. Drawn by `shaders/br_zone_wall.gdshader`: unshaded, cull off, stripes about every 12 m,
  rising bands, stronger up close, Bayer-dithered. Hidden at radius 0.
- Not yet: rejecting centres over lakes or steep slopes (the server and clients would need the same
  height data); a terrain tint outside the circle (shader globals).
