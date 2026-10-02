# Perf: camera and per-frame rays reuse one query and one exclude array (#221)

## Rule
- A ray cast every frame or tick goes through a `Core.RayQuery` field (`_ray.Cast(space, from, to,
  mask, exclude)`), never `space.IntersectRay(PhysicsRayQueryParameters3D.Create(..., new Array<Rid> { ... }))`.
- Exclude arrays are cached and never changed after being passed: `FootPlayer.SelfExclude` (just
  the body), `TrainRids()` (the body and its truck/bus sections; nulled in `FootPlayer.Heavy` where
  `_sections` changes), `WithShell(rids, shell)` (plus a doorway's shell), `SeatExclude(who)`
  (passenger camera). `RayQuery` only re-sends the exclude array when a different array comes in,
  so changing one in place needs `ray.Forget()` before the next cast (`Hearing.Occluded` does this).
- `FootPlayer` cameras (third person, ride/chase, flight, seat, crash) share `_camRay`; the truck's
  ground rays use `_groundRay`.

## Why
#221 client investigation #13: a new `PhysicsRayQueryParameters3D` + `Array<Rid>` per camera ray per
frame (1 to 3 rays a frame, every client, every mode), plus the item highlight / aim / photo rays,
the throw arc (one query per step), the hearing occlusion (per audible source per frame) and the
NPC arrival rays. PR #321 (with the snapshot changes), `--drivecheck --chunks fixture:hairpin --traffic 35
--perflog 60`, steady part t 50-90 s, `GC.GetTotalAllocatedBytes`, 2 runs each, back to back:

| | allocated | gen0 / gen1 per min | physics ms p50 |
|---|---|---|---|
| main | 96.1, 96.3 MB/min | 9 / 9 | 1.65, 1.59 |
| #321 | 92.3, 93.2 MB/min | 9 / 9 | 1.76, 1.67 |

About 3.5 MB/min less for one car (-4 %); the saving scales with players (snapshot) and with rays
cast (third person on foot, passengers, aiming). On foot in town among 12 swarm bots the allocation
is dominated by terrain streaming (~700 MB/min) and the difference is within noise.

## Same logic, preserved
- Same rays: same from/to/mask, the same excluded RIDs (third person: the body; chase: the body and
  its train, as `ExcludeTrain` built it; doorway: plus the shell; seat: the body, the vehicle and its
  `CollisionObject3D` children). The query's other fields keep their defaults, as `Create` did.
- `TrainRids()` must be invalidated wherever `_sections` changes (`_trainRids = null`), or the
  camera and the section ground rays hit the newly hooked trailer.
- `SeatExclude` rebuilds on a new vehicle, a new train array of that vehicle, or a new child count.

## Migrating old code / open branches
- `grep -rn "PhysicsRayQueryParameters3D.Create" src`: in per-frame or per-tick code, add a
  `private readonly Core.RayQuery _ray = new();` (static for static callers) and cast through it;
  `new Array<Rid> { player.GetRid() }` becomes `player.SelfExclude`.
- `ExcludeTrain(rids)` is gone: use `TrainRids()` (do not add to it).
- Open PR #269 touches `FootPlayer*.cs`, `ItemController`, `ThrowAim`, `NpcArrival` in other lines;
  a conflict there keeps both sides.

## How to check
- `tools/test.sh quick` (`--synccheck --world flat`, driving checks).
- By eye: third person through an open door (the lens follows into the room), a truck's chase camera
  along its trailer (no pull-in on its own trailer), a passenger seat's chase camera.
