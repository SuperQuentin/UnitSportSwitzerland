# Ground under a point: `World.GroundQuery.Under` (#221)

## Rule
- The height of what a vehicle stands on under a point is `GroundQuery.Under(self, ray, exclude, p,
  terrain, pastPlayers)`: a ray from 3 m above to 6 m below with `self.CollisionMask` minus the tree
  colliders, else `terrain.TryGetHeight`, else `p.Y`. Never another private copy.
- Pass a reused `Core.RayQuery` and a cached exclude array (`TrainRids()` for a driven truck,
  `_groundExclude` for a parked `VehicleBody`).
- `pastPlayers: true` looks past up to 4 players on the ray (a person standing in a parked bus, #162).

## Why
#221 duplicate cluster #12: `FootPlayer.Heavy.GroundUnder` and `VehicleBody.Ground` were the same ray.

## Same logic, preserved
- Same ray, mask, fallback. `VehicleBody` keeps the player skip (its retry excludes the body plus the
  players hit, as before); the driven truck still does not skip players, as before.

## Migrating old code / open branches
- `grep -rn "Vector3.Up \* 3f, p + Vector3.Down \* 6f" src`: replace with `GroundQuery.Under`.
- The six `TryGetHeight` wrappers elsewhere are not migrated (terrain only, no ray).

## How to check
`tools/test.sh quick` (`--truckcheck`); a parked bus with a player standing in it stays on the road.
