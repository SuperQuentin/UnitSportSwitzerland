# Parked vehicles: nothing per frame at rest (#221)

## Rule
- `VehicleBody._Process` does no per-frame work for a vehicle standing still. A parked truck or
  bus is dressed once at rest (`_dressedAtRest`). It is dressed again when it moves (`Velocity != 0`)
  or its `Wrecked` changes. A new per-section visual state must join that key, or be set on the rig
  where it changes.
- No `FindChildren`, LINQ or `$"Section{k}"` per frame. Use the cached `_heavySections` (dressing)
  and `_standSections` (posing).
- Ground rays reuse `_groundQuery` (one `PhysicsRayQueryParameters3D`, one exclude array). Never
  `PhysicsRayQueryParameters3D.Create(..., new Array<Rid> { ... })` per ray.
- `StandOnGround` re-reads the ground every 2 s once it has stood at rest. It reads it every 0.25 s
  while settling and every frame while rolling.

## Why
A parked truck or bus used to run, every frame and for ever: `FindChildren` + LINQ + `Dress`,
plus 4 ground rays a second, each with new query objects. This is a static argument (work skipped,
allocations removed), with no scene of many parked heavies measured (PR PRNUM). `--heavynet a|b` on
loopback: the watcher sees the same parked train (trailer, truck, boxes) before and after.

## Same logic, preserved
- At rest, every value `Dress` and the lamp lines set is constant: the flags come from spawn, the
  wheel spin is frozen at speed 0, and the body roll comes from the last step. One dress gives the same picture.
- Remote copies key on the replicated `Velocity`, which is exactly zero once the authority sleeps.
- Trap: anything that changes a parked rig without moving it (a bus's doors worked from outside,
  PR #169) must set the rig itself, as #169 does for `DoorsOpen`, or reset `_dressedAtRest = null`.

## Migrating old code / open branches
- PR #169 (`feat/162-walk-in-bus`) edits `VehicleBody`:
  - its bus-door block in `_Process` sets `bus.DoorsOpen` every frame. Keep that, but use
    `_heavySections` instead of `bus.GetChildren().OfType<HeavyRig>()`;
  - `FootPlayer.WatchGuests(this, Ride, _guests, new PhysicsBody3D[] { this }, k => ...)` in
    `_PhysicsProcess` allocates an array and a closure every tick: hoist both into fields;
  - `Visual?.GetNodeOrNull<Node3D>($"Section{k}")` should read `_standSections[k]`;
  - `StepDriverless` passes `this` to `Surfaces.At` (`docs/notes/audio/perf-surface-grid.md`).
  - conflicts: the `_Process` heavy block (keep the `restDressed` guard around the dress loop) and `Ground()`.
- `grep -n "FindChildren\|PhysicsRayQueryParameters3D.Create" src/Vehicles`: none of these may run per frame.

## How to check
Run `--heavynet a|b` on a loopback server (see the player note `trucks-buses`). The watcher's
parked train, lamps and wheels must read the same as before.
