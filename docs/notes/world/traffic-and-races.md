# Traffic around a race (#85)

Traffic stays local and cosmetic per client (nothing new is replicated) but solid. `Traffic.Obstacles`
is every `FootPlayer` (local, remote, race NPCs) with its `WorldVelocity`; `--drivecheck` sets its racers.

## Traffic side (`Traffic.StepCar`)

- **Sees, then reacts.** A driver reacts to a racer only with line of sight (`Sees`: one ray per car
  every 0.2 s from 1.2 m above its head, terrain/buildings hide, trees/cars/people do not; 250 m;
  remembered 1.5 s; ponytail: one sight per car, not per racer) and only after its reaction time
  (`Vehicle.Reaction`, 0.5..1 s per driver).
- **Seen early** (calm): someone fast closing from behind or coming at it on a narrow two-way road
  (< 7.5 m): pull to the right edge (`Pull`, 0.8 m/s, body kept on the tarmac) and stop there until it
  has gone by (`stopFor`, 3.5 m/s²); on a wide road pull right and slow to 60%. At a junction (next one
  within 60 m, `Route.NextJunction`) it waits short of it (`Holding`) while someone fast is about to
  pass through; committed (< 5 m) it clears it.
- **Seen late** (under 2.5 s to meet, out of a blind bend or over a crest): startled for 1.2 s — brakes
  to a stop at 8 m/s² for someone coming at it or across its way (a lift only for one from behind),
  swerves for the verge at 2.2 m/s (never towards a racer on that side), wobbles ±0.12 m.
- One waits, one goes: a car standing behind something standing in its lane turns round after 5 s;
  a car waiting for a race is not "stuck" (no despawn in front of the racers). Cars never spawn within
  60 m of a player. Race grids clear local traffic within 150 m (`Traffic.ClearAround`, from
  `RaceManager.Setup` and `--drivecheck`).
- Junctions: see `traffic-trains` (trimmed junction ends joined; before, cars turned round mid-junction).

## Racer side (`AutoPilot.Traffic`)

- Traffic joining: `Crossing` reads each car's lane 6 s ahead (`Traffic.CarView.Path`) and slows to
  stop 10 m short of where it comes onto the route (not for a car `Holding` for the race).
- Traffic gets more room than a rival (+0.3 m clear, +0.4 m beside), a follow gap +4 m and a follow
  speed from which this car stops even if the traffic car stops too.
- Bounds are applied at the obstacle (`shift` = line offset there vs at the aim point), and for a
  standing car even while it is out of the way; crossing bounds from things close together ahead
  mean no way through: stop short of the nearer.
- A standing traffic car is passed at 50 km/h at most (at 90 the car's own line swung it 2 m back
  into the traffic in the last 15 m); needed clearance 2.0 m centre to centre at a crawl, 2.6 at speed;
  not across yet: crawl in with the metres to get across (0.15 m per metre), else stop and back off
  (not with a car right behind).
- A traffic car creeping (< 3 m/s) counts as standing.
- Wrecked / thrown off: a race NPC retires (`RaceNpc.OnAnnounced` → `RaceManager.RetireNpc` → DNF,
  `[race] #N X crashed out: retires`); in `--drivecheck` such a car is `OUT`.
- Crash log: a THROWN line prints the traffic car's state and the pilot's last 2 s (`AutoPilot.Trail`,
  `rule` = the source line that set the cap).

## Numbers (`--drivecheck --at 2518038,1167321 --finish 8200 --cars 0,1,3,4,12,6 --seconds 600`)

| traffic 35 | finishers | out | traffic impacts | resets | at pace | off tarmac m | blocked edge m |
|---|---|---|---|---|---|---|---|
| before (3 runs) | 1, 2, 1 | 3, 4, 5 | 103, 62, 85 | 46, 32, 20 | 13, 14, 15% | 914, 961, 620 | 131, 105, 76 |
| after (3 runs) | 0, 2, 2 | 6, 4, 4 | 19, 8, 15 | 19, 5, 15 | 8, 11, 13% | 880, 1189, 1099 | 59, 64, 52 |

Traffic 0: before 6/6, 41% at pace, 300-321 s; after 6/6, 38%, 299-352 s, 9 resets. Target (4-5 of
6 finishing) NOT reached: traffic impacts -80% and resets -50%, but half the OUTs are now trees on
the fast downhill after 5 km (racers going off at 80-130 km/h near slow or standing cars) and cars
thrown against traffic at unseen junctions (traffic that had not seen the racer pulls out; racers
must still cope). Traffic is random: runs differ a lot.
