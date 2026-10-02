# Perf: no figure mesh rebuilt per frame

## Rule
- **Never build a new `ArrayMesh` per frame** for an animated figure or rig part. A figure whose
  pose is computed (gait, arm blend, dance) keeps ONE mesh and rebuilds it in place:
  `HumanMeshBuilder.BuildStride(..., into: _mesh)` / `BuildPosed(..., into: _mesh)` (or
  `MeshScratch.BuildInto(mesh)` for your own geometry, after `Clear()`).
- **Rebuild only when the pose changes.** Key what the mesh is built from (pose kind, speed
  rounded to the cm/s, phase, arm pose + blend, dance params, hat, the palette with its outfit and
  skirt wind (#251), the `MeshInstance3D`) and skip
  the build when the key is equal. The hand mounts (`HandLocal`, held items) are recomputed on
  every key change even while a throttled mesh waits: joint math only, and `--synccheck` holds the
  remote hand to 0.25 m of the owner's (`FootPlayer.ApplyFootPose`, `FootPoseKey`).
- **Remote figures are throttled:** beyond 40 m or outside the main camera's view cone, at most
  15 Hz (`FootPlayer.HoldRemoteFigure`). Never freeze them completely: mirrors (`CabMirrors`),
  door portals and photos render other cameras.
- **A pose that is a function of one quantised value is cached, not rebuilt:** cyclist cranks and
  legs by crank step (`Cyclist.Steps`, shared by every rider with the same palette and outfit), a seated
  driver by (wheel /0.03 rad, throttle /8, brake /8) through `HumanMeshBuilder.DriverBody(cache, ...)`
  (`CarRig`, `HeavyRig`). Something that only turns (a wheel, a needle, a rotor) is a node rotated
  under a pivot, never a rebuilt mesh.
- Stateful per-frame steps (`StepArmPose`, `StepDance`, phase integration) still run every
  frame: only the mesh build and its mounts are skipped.

## Why
#221, branch `feat/221-avatar-mesh`;
client in Riddes among 24 swarm bots + a second client, vsync off, 75 s steady state:

| run | frame mean / p99 ms | render CPU ms | gen0 GC/min |
|---|---|---|---|
| on foot, standing, before → after | 6.66 / 33.6 → 4.70 / 11.1 | 1.18 → 1.03 | 441 → 193 |
| second run, before → after | 5.83 / 16.7 → 5.32 / 16.7 | 1.15 → 1.08 | 541 → 173 |
| offline road bike, before → after | 1.53 / 2.78 → 1.43 / 1.85 | 0.43 → 0.37 | 126 → 30 |
| offline car (driver) | unchanged (1.42 → 1.48) | 0.37 → 0.39 | 36 → 34 |

Round 2, GPX replay of 8 runners at Riddes (`--gpx` ×8, `--perflog 80`, steady part t 47–80 s,
allocation and GC pause from `GC.GetTotalAllocatedBytes` / `GetTotalPauseDuration`): allocated
1338 → 386 MB, GC pause 154 → 110 ms, gen0 207 → 60 per minute. gen1 rises 9 → 55 per minute: with
far less short-lived garbage almost every remaining collection is a gen1 one (the runtime's choice);
the total pause still drops. Do not read the gen1 column alone for this change.

## Same logic, preserved
- The figure geometry for a given pose is byte-identical (same builder, same inputs); only the
  number of times it is built changes.
- Accepted visual deltas: a standing figure ignores speed changes under 0.005 m/s; a remote figure
  far away or off screen animates at 15 Hz; cranks and legs move in 2.8° steps; a driver's hands
  follow the wheel in 0.03 rad steps (they did before too, from the key change frame); a skirted cyclist ignores wind changes under 0.005 m/s.
- Traps: a new input to the figure (a new replicated field that changes the mesh: an accessory, a
  palette change, a new arm pose parameter) MUST go into `FootPoseKey`, or the figure keeps its old
  look until the pose next changes. A new `_walker` instance is part of the key, so a rebuilt visual
  is always drawn. Do not modify a mesh returned from a cache (`Cyclist` steps, `DriverBody`): it is
  shared.

## Migrating old code / open branches
- `grep -rnE "BuildStride\(|BuildPosed\(|BuildCranks\(|AppendDriver\(.*WheelTurn|\.Mesh = .*\.Build\(\)" src`
  inside `_Process`, `Animate*` or `Apply*Pose` methods: each hit that runs per frame must pass
  `into:` a mesh kept in a field, or be cached by a quantised key, and be skipped when the key is
  unchanged.
- `ApplyFootPose` (FootPlayer) was restructured: the `switch (PoseKind)` now sits inside
  `if (key != _poseKey && !HoldRemoteFigure())` and writes `_poseMounts` instead of a local
  `mounts`. A branch that added a pose case or a new argument there: re-add it inside the switch
  with `into: _poseMesh ??= new ArrayMesh()` and add the new argument to `FootPoseKey`.
- A branch that adds an `ItemArmPose` (e.g. #222's throw) needs nothing more: arm and blend are in
  the key.
- Open PRs touching these files when this landed: check `gh pr list --json number,files` for
  `HumanMeshBuilder.cs`, `FootPlayer.cs`, `Cyclist.cs`, `CarRig.cs`, `HeavyRig.cs` (#220, #222,
  #225, #223, #169 at the time). Their hunks are next to, not inside, the changed lines; a conflict in
  `ApplyFootPose` resolves as above.
- Migrated in round 2 (PR "figure meshes rebuilt in place"): `Gpx/Runner` (one `_bodyMesh`, key
  (speed cm/s, phase): a paused ghost builds nothing), `Interiors/PortalDemo` (walker in place),
  and the skirted cyclist (`Cyclist._Process`: `HumanMeshBuilder.Build(..., into: _riderMesh)`
  keyed on the wind rounded to the cm/s, so it rebuilds in place while the wind moves and stops
  once it settles). `HumanMeshBuilder.Build` takes `into:` like `BuildStride`/`BuildPosed`.
  A branch that edits those lines keeps the key check and the `into:` argument.
- Still building per frame: `AvatarPreview` only (a preview tool, not the game).

## How to check
- `--perflog` on a client among on-foot remotes (swarm walkers in Riddes): `gc0` per minute in
  `frames.csv` stays near the numbers above, not 5+ collections a second.
- Visual: `tools/useanimcheck.sh` (remote arm poses and hat on peer B); `--ride bike,30 --view third`
  for the cranks; the avatar preview `--crank` for the crank direction.
