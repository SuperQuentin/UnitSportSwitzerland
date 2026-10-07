# Collision matrix: every collidable thing, rammed by a car and a walker (#699)

- **Check:** `--collidecheck [--movers car,walk] [--targets a,b] --world flat`
  (`src/Core/Collision/CollisionMatrixProbe.cs`).
  - Every target is run into by every mover, on several lanes.
  - It prints one line per run, then `[collide] RESULT`.
  - The full matrix is 169 targets, 884 runs, about 4.5 min headless at `--fixed-fps 60`.
  - `--targets` filters by substring of `Category/Name`, e.g. `machines/`, `doors/`, `a320`.
  - `--movers` takes any ride name `RideProbe.KindNamed` knows (`bike`, `truck:2`, `forklift`,
    `Helicopter`...), or `walk`.
- **Sandbox:** `--collidesandbox [car|walk|<ride>] [--targets ...] --world flat`
  (`CollisionSandbox.cs`), windowed. It lays out the same catalog in labelled rows, one per category,
  to drive into by hand. Vehicles are parked under a `VehicleManager`, so E gets into them.
  `--sandboxshot out.png` takes one picture from above and quits.
- **The catalog** is `CollisionTargets.All()`, enumerated from the registries, so a new car, gadget,
  piece or kind shows up by itself:
  - every `Rideable.Create` vehicle, parked as a `VehicleBody`;
  - every `PlacedKind`, through `PlacedObjects.VisualOf`;
  - every allowed `PieceKind` × `BuildMaterial`;
  - a pallet;
  - every `DoorLeaf` hang.
- **To add a kind of thing that is in no registry:** add a `yield return` in `All()`. That is a
  `Func<WorldOrigin, Transform3D, Node3D>` building it at a world transform, plus a `Drawn` builder
  when the spawned node draws nothing headless (a parked vehicle does not).
- **Lanes:**
  - `side`: across the drawn shape's short axis at its centre.
  - `side-` / `side+`: the same lane at ±35 % of the length, when the target is longer than 4 m.
  - `end` / `end-` / `end+`: along the long axis, the off-centre pair at ±35 % of the width, when
    wider than 4 m (wings).
  - The car drives at 7 m/s, below `FootPlayer`'s `ThrowSpeed`, so a hit is a knock, not a crash.
- **The verdict compares what is drawn with what collides:**
  - The drawn triangles are clipped against the mover's corridor (its width, 0.1 m up to its roof or
    head). That says whether anything drawn is really in the way: a car passes under a wing for real.
  - **ghost:** drawn in the way, gone through untouched.
  - **invisible wall:** nothing drawn in the way, stopped anyway.
  - `blocked`, `over` (went over it, touching) and `clear` are right.
  - A run that goes wrong is run once more, and the second outcome counts; it is marked "flaky"
    when the two differ.
- **Known gaps** are the `Gaps` table in `CollisionTargets` (target, mover, lanes, why).
  - A run listed there may go wrong; a run not listed must not.
  - A line none of whose runs goes wrong any more fails the check: the gap is fixed, take the line out.
  - Lanes are tolerated per line because one or two lanes depend on which runs share a batch (the
    A320's car `end`).
  - Listed when it was written (Oct 2026):
    - the facade barn pair and the garage roll-up leaves are never solid (the facade's building
      collision is);
    - cars and walkers go through parked airliners' fuselages (A320, military cargo plane, AN-124),
      but only when parked under a `VehicleManager`, as in the game;
    - the excavator's parked box leaves out the boom, stick and bucket; the loaders', mini
      excavator's and telehandler's leave out one end;
    - the helicopter's parked box is solid under its tail boom;
    - the campfire's cylinder stands round its low logs.
- **Traps found on the way:**
  - **Park vehicles under a `VehicleManager`.** A walkable vehicle (bus, airstairs, steamer)
    excepts any player within 1 m of its deck from its hull (`FootPlayer.WatchGuests`). The player
    then walks on its own copy of the deck, which `ScanDecks` builds only for vehicles under
    `VehicleManager.Instance`. Without the manager, walkers went straight through buses.
  - **Contact on a walkable deck** is a slide collision with the walker's own `Deck_*` body, not the
    vehicle.
  - **A vehicle parked at world (0, 0) on the flat world ended up on top of its neighbour 200 m away**
    within a second. The grid starts one cell off the origin. Not chased further.
- **Not covered yet:** buildings and facade doors as the game stands them (one-sided concave
  building collision, the facade with its leaf). Those need tile data (`fixture:garage`); for now,
  the `GarageProbe`, `rampnetcheck` and `baycheck` checks drive through them.
