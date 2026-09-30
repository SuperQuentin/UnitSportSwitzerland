# Cars and motorbikes collide as a box (hull), not only as the capsule that carries them

- A mounted player is a `CharacterBody3D` whose capsule is sized by the ride (`BodyRadius`, 0.85 m
  for a car). The capsule rides the ground well — it glides over the 1 m terrain lattice where a box
  would snag — but two capsules only meet when their centres are 1.7 m apart: two 4.2 m cars sank a
  third of their length into each other (reported with a screenshot).
- `FootPlayer.FitHull` adds a `CollisionShape3D` "Hull": the ride's `ParkedBox` from `HullLift`
  (0.45 m, above the lattice bumps) to the roof, for every ground vehicle (`IsVehicle` and not a
  `Flyer`). `AlignHull` turns it to the body's actual yaw from `BodyPose` every frame, so a drifting
  car is a sideways box. It exists on the owner (its own physics, trees, walls) and on every remote
  copy (`FitRemoteBody`), so others hit the car they see.
- Verified on a recorded 4-car pack descent: closest nose-to-tail centre distance 4.19 m for 4.2 m
  cars (touching, never inside), side by side 1.70 m for 1.7 m width; lap times unchanged
  (118–120 s), so the raised box does not catch the terrain.
- GPX `<us:yaw>` is in radians (east = −sin, north = cos), useful when post-processing recordings.
