# Traffic around a race (#85, #159)

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

- **Traffic lights and lanes** (#353, `traffic-trains`): a racer ignores them, as it ignores yield
  lines; traffic stops at red and moves into pocket lanes (cars at a red light stand still: a
  racer passes them as standing traffic). Traffic waiting for a race (`Holding`) still holds at a green.
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

## #159: races in traffic, part 2

- **Giving way at junctions** (`Route.NextJunction(...).Yields`, `LaneGraph.GivesWay`: a road meeting a
  more important class, also across a trimmed-junction connector): a driver at the line looks 9 s
  (not 7) and needs no sight line or reaction time. Also for a racer coming along the road this car is
  about to turn INTO: beyond the junction it counted as "on its road", the oncoming rule waited for
  sight and reaction, and the car stood in the junction mouth in front of racers at 120-150 km/h
  (Mollendruz 7461 m, three racers out in one run). ponytail: class only; the stored yield arms of
  #117/#121 (`GiveWay`, traffic among itself) do not look at racers yet.
- **Spawns** keep 60 m + 4 s of each racer's speed clear (60 m is 1.4 s at 150 km/h). A traffic
  car's age is in the crash line.
- **Sensing across bridged junctions**: the racer's sensing boxes are placed on the centre
  interpolated between points; on the point before them they left the middle of a bridged gap
  (points up to 18 m apart; 60 such gaps on Sainte-Croix) unswept, and a car standing on the
  junction's connector was hit without ever being sensed. `--drivecheck` prints `route gaps`.
- **Racers** (`AutoPilot`): no racer-vs-racer pass above 90 km/h; traffic is passed only from a speed
  the car can still drop back from; a car coming the other way at a walk is a standing one; brake
  pedal capped at 90% of the rear-lockup limit (`PedalMax`), gas feathered when the rear steps out,
  an unplanned slide too fast lifts and brakes; `EdgeGuard` at the hands' own gain at speed; backing
  out never over a blocked edge; a reset uses `PlaceAt` (the car kept its ditch heading and drove off
  again); the profile keeps up to 6% in hand above 80 km/h and 5% on steep descents; skill spread
  ×0.90..1.03 corner, ×0.85..1.04 braking share.
- `--drivecheck --to E,N`: a course between two points (shortest way by road). `PACE`: each
  finisher's time against a skill-1 driver of the same car on a clear road, ace vs the rest.
  `--drivecheck` turns the floating-origin shift off for its run (#185 moved the origin mid-build;
  #259).
- **Test base**: the Col du Mollendruz is no longer a required base for driving checks: #221 moves
  them to fixture courses (`--systems`). The numbers below are on real tiles
  (`--chunks <terrain_chunks>`), kept as a record.

### Numbers, traffic 35, cars 0,1,3,4,12,6, 3 runs each

Before = c545239 (#155), v6 = the WIP handed over + feathered pedals, v8 = v6 + the fixes above
(measured on the pre-main base: on main, #214's windscreen throw retires racers in side contacts,
see the PR). Mollendruz `--at 2518038,1167321 --finish 8200`; Sainte-Croix up `--at 2532394,1184451
--to 2528903,1185924`, down the other way, `--seconds 900`.

| course | | finishers | out | traffic impacts | blocked edge m |
|---|---|---|---|---|---|
| Mollendruz | before | 3, 0, 0 | 3, 5, 4 | 10, 15, 25 | 32, 33, 57 |
| | v6 | 5, 3, 4 | 1, 3, 2 | 18, 34, 38 | 31, 32, 8 |
| | v8 | 6, 5, 6 | 0, 1, 0 | 7, 11, 7 | 1, 3, 25 |
| Sainte-Croix up | before | 2, 1, 3 | 4, 5, 3 | 7, 8, 47 | 147, 291, 281 |
| | v6 | 5, 4, 4 | 1, 2, 2 | 2, 48, 7 | 66, 54, 32 |
| | v8 | 5, 3, 2 | 1, 2, 4 | 4, 7, 5 | 27, 32, 54 |
| Sainte-Croix down | before | 3, 2, 1 | 2, 4, 5 | 7, 5, 14 | 136, 176, 112 |
| | v6 | 5, 4, 3 | 1, 2, 3 | 15, 7, 24 | 69, 67, 32 |
| | v8 | 3, 3, 4 | 3, 3, 2 | 17, 17, 7 | 30, 93, 38 |

v8 retirements: traffic 4 (of 9 runs; v6: 11 in 8), trees 9 (Sainte-Croix, 100-160 km/h, no
traffic around: the speed profile and line there), others 2. Blocked-edge metres are many short
excursions while sliding off or squeezing past standing traffic; 0 m is not reached. PACE (ace vs
others, %): v8 Mollendruz 58.3/61.5, -/61.0, 67.7/65.2; down -/62.8, 54.6/63.0, 67.5/64.6; up
57.9/56.2, -/58.7, -/53.0: in traffic the ace is not measurably faster.
