# Perf: one player snapshot per physics tick (#221)

## Rule
- Code that runs per physics tick and needs "every player / racer / NPC, where it is and how it
  moves" reads `PlayerSnapshot.Of(GetTree())` (`src/Player/PlayerSnapshot.cs`): a list of
  `(Player, Pos, Vel = WorldVelocity, Ride)` built once per tick by the first caller and shared.
  Never `GetTree().GetNodesInGroup(FootPlayer.Group)` (a new Godot array each call, plus a
  `StringName` from the string) or LINQ over it in `_PhysicsProcess` or anything it calls.
- Do not keep the list past the call, do not modify it, read it from physics code only (in
  `_Process` it is up to a tick old).
- Lists handed to per-tick consumers are reused, not rebuilt: `Traffic._obstacles` (refilled from
  the `Obstacles` callback), the callback's own list in `ClientWorld`, `CombatManager._targets`.
  Ray queries per tick reuse one `PhysicsRayQueryParameters3D` and one exclude array
  (`CombatManager._crossQuery`, `_tracerQuery`, `_exclude` refilled per tick per shooter).
- A group only counted: `GetNodeCountInGroup(StringName)`, not `GetNodesInGroup(...).Count`.

## Why
#221 client investigation #9 (`GetNodesInGroup` + LINQ per tick per vehicle). `--perflog`, same
machine, back to back, allocation and GC pause from `GC.GetTotalAllocatedBytes` /
`GetTotalPauseDuration` over the steady part:

| run (steady part, t 47 to 90 s) | allocated | GC pause | physics ms p50 / mean | RESULT |
|---|---|---|---|---|
| `--drivecheck --chunks fixture:hairpin --traffic 35`, main | 75.7 MB | 33.8 ms | 1.59 / 2.29 | same classification |
| same, this branch | 72.8 MB | 36.2 ms | 1.59 / 2.23 | same classification |

Small at one player and 35 cars (the probe sets its own `Obstacles`, so only the `Traffic` side runs
there); the scan it replaces grows with players × callers, so a race among traffic, or a dogfight,
is where it counts. `--combatcheck` (plane and paraglider) passes on both.

## Same logic, preserved
- Users: `Traffic` obstacles (via `ClientWorld`), `CombatManager.Targets` / `WingHit` /
  `ExcludeFor`. The same players, in the same group order; positions are the ones at the tick's
  first `Of` call, so a player stepped later in that tick is seen where it was one tick earlier
  (before, each caller read the live node; node order made that arbitrary anyway).
- Combat leads targets with `fp.Velocity` read live, as before, NOT the snapshot's `WorldVelocity`:
  a remote target's `Velocity` is zero, so remote players are not led. Switching to `s.Vel` would
  fix that, but it is a gameplay change for its own issue.
- `WingHit`, `ExcludeFor` read `Ride`, `GlobalTransform` and the RID live from `s.Player`.
- Origin shifts (`OriginShifter`) run in `_Process`, between physics ticks, so a snapshot never
  spans a shift. Code that shifts the origin inside a physics tick must rebuild it (call `Of` on a
  new tick only; add an invalidate if that ever happens).
- `DriveProbe` still sets its own `Obstacles` LINQ lambda (probe only); `Traffic` copies whatever
  the callback yields into its reused list, so any `IEnumerable` still works.

## Migrating old code / open branches
- `grep -rn "GetNodesInGroup(FootPlayer.Group)\|GetNodesInGroup(Group)" src`: in per-tick code,
  replace `foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group)) if (node is FootPlayer p ...)`
  with `foreach (var s in PlayerSnapshot.Of(GetTree())) { var p = s.Player; ... }` and use
  `s.Pos` / `s.Vel` / `s.Ride` instead of `p.GlobalPosition` / `p.WorldVelocity` / `p.Ride`.
- Migrated in round 3: `FootPlayer.OtherVehicles` (slipstream, into the reused `_otherVehicles`;
  `RideGround.DraftBehind` takes a `List` now), `FootPlayer.Overlap.AnyPlayerNear`,
  `RaceNpc.Bodies` (reused `_bodies`), `RaceManager.Others` (one static list refilled per call:
  read it within the pilot's `Drive`, never keep it). Their positions are the tick's snapshot ones.
- Left as scans on purpose (not per tick): `FootPlayer.DancersAround` (every 0.5 s, from `_Process`,
  where the snapshot may be a tick old), `RaceNpc` `--npccheck` (every 5 s, probe), `RaceManager`'s
  GO print (once), `VehicleBody._Ready`, `FootPlayer.Deck`/`Passenger` riders (on events),
  `PlayerHits`, `ThrowHits` (per throw).
- `Traffic.Obstacles` keeps its type `Func<IEnumerable<(Vector3 Pos, Vector3 Vel)>>`. A branch that
  edits the old `Obstacles?.Invoke().ToList()` line keeps the new three lines (`_obstacles` refilled).
- Open PRs touching these files when this landed: #269 (`CombatManager.cs`: the `Shot`/`ShotFrom`
  RPCs and `Create`, not the lines changed here; `ClientWorld.cs`), #281 (`ClientWorld.cs`, away
  from the traffic block). Conflicts, if any, keep both sides.

## How to check
- `--drivecheck --chunks fixture:hairpin --traffic 35 --perflog 60`: the RESULT line as on main,
  `gc0` per minute in `frames.csv` not above main's.
- `--combatcheck` (plane) and `--combatcheck --craft paraglider`: RESULT pass (aim, lead, hits).
