# Cars and motorbikes collide as a box (hull), not only as the capsule that carries them

- A mounted player is a `CharacterBody3D` whose capsule is sized by the ride (`BodyRadius`, 0.85 m
  for a car). The capsule rides the ground well — it glides over the 1 m terrain lattice where a box
  would snag — but two capsules only meet when their centres are 1.7 m apart: two 4.2 m cars sank a
  third of their length into each other (reported with a screenshot).
- The rule (from the owner): **nothing ever goes into another model**. `FootPlayer.FitHull` adds two
  boxes, `HullLow` and `HullHigh`, measured from the vertices of THIS model's drawn mesh
  (`Avatar.MeshBounds.Split`, cut at 55 % of the height): a car's body and its cabin, a motorbike's
  frame and its rider — per model, an AE86 is not an NSX. From `HullLift` (0.45 m, above the lattice
  bumps) up. Fitted at the end of `RefreshVisual`, so on the owner (its own physics, trees, walls) and
  on every remote copy, for every ground vehicle (`IsVehicle`, not a `Flyer`).
- `AlignHull` applies the body's whole pose (`BodyPose`: drift yaw, pitch over a crest, a flip) to
  both boxes every frame; a leaning two-wheeler keeps yaw and pitch only, or its inside corner would
  touch the road at 50° of lean.
- The hurtbox (shots) is the same two-box split, parented to the visual; parked cars measure their
  own model too (`Car.ParkedBox` → `Measured`). `--hitboxcheck` compares the union of the hurtbox
  boxes with the drawn bounds.
- Verified on a recorded 4-car pack descent: closest nose-to-tail 4.47 m (FD3S/FC3S, the sum of their
  drawn half-lengths: touching, never inside), side by side 2.03 m (mirrors included); lap times
  unchanged (114–120 s), so the raised boxes do not catch the terrain. `--hitboxcheck` ok.
- GPX `<us:yaw>` is in radians (east = −sin, north = cos), useful when post-processing recordings.
