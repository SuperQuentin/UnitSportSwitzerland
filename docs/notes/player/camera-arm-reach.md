# Camera arm through doors: one `ArmReach` (#221)

## Rule
- A camera arm that pulls in short of walls and reaches through open doorways calls
  `FootPlayer.ArmReach(from, to, exclude, margin, scale, min, out through, out across)`; never copy the
  door/near/far ray block again. A hit at distance d gives `clamp((d - margin) / span * scale, min, 1)`.
- On foot: `ArmReach(pivot, wanted, SelfExclude, 0.25f, 1f, 0.1f, ...)`; chase: `ArmReach(from, to,
  TrainRids(), 0f, 0.85f, 0.15f, ...)`. The easing (snap in, ease out) stays with each caller.

## Why
#221 duplicate cluster #8: `UpdateThirdPersonCamera` and `UpdateRideCamera` had the same ~30 lines
(door sill ray, ray across the door's map, plain ray), differing only in the shorten rule.

## Same logic, preserved
- Float-identical: `(d - 0f)` is `d` and `x * 1f` is `x` in IEEE, and the far-side length is summed
  in the same order (`t * span + far`, then the margin). Same rays, same exclude arrays
  (`WithShell` adds the doorway's shell), `through = 2` and `across = Identity` when no door.

## Migrating old code / open branches
- A branch that edits either camera's pull-in block: move the change into `ArmReach` (both cameras
  get it) or into the caller's margin/scale/min. Open PR #269 does not touch these lines.

## How to check
`tools/test.sh quick`; by eye, third person and a car's chase camera backing through an open door.
