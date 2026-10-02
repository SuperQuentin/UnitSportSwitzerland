# MathX: the shared small maths helpers (#221)

## Rule

- Never write another private `Flat`, flat length, exp-damping weight or ±π wrap. Use `Core/MathX`:
  - `MathX.Flat(v)` = `new Vector3(v.X, 0, v.Z)`; `MathX.FlatLength(v)` = `new Vector2(v.X, v.Z).Length()`;
    `MathX.FlatDistance(a, b)`.
  - `MathX.Damp(rate, dt)` = `1f - Mathf.Exp(-rate * dt)`: `x = Lerp(x, target, MathX.Damp(rate, dt))`.
  - `MathX.WrapAngle(a)` = `Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi)`.
- A smoothstep is `Mathf.SmoothStep(0f, 1f, t)`, never `t * t * (3f - 2f * t)` again.
- `MathX` stays pure (no engine calls): tier 0 links it (`tests/.../MathXTests.cs`, GodotSharp's managed
  `Vector3`/`Mathf`, no engine loaded). Add a test there for any new helper.

## Why

#221 cluster #11: `Flat` ×5, flat length ×3, exp damping ~74 inline, angle wrap 26, smoothstep 8.
This PR moved 32 damping weights, 18 wraps, 5 private `Flat`/`FlatLength` helpers (23 calls), 4 smoothsteps and `RoadMeshBuilder.Smooth`
in 32 files; no behaviour change.

## Same logic, preserved

- Bit-identical floats: each helper is the exact inline expression (tier 0 asserts `Damp == 1f - Mathf.Exp(-rate * dt)`
  and `== 1f - Mathf.Exp(-dt * rate)`, `WrapAngle == Mathf.Wrap(...)`, `SmoothStep(0, 1, t) == t*t*(3-2t)`).
- Only converted where the result is identical:
  - `1f - Mathf.Exp(-dt / tau)` is NOT `Damp(1f / tau, dt)` (a division is not a multiply by the reciprocal):
    left inline (`HeavyDriveline`, `HeavyTrain`, `Hearing`, `ReverbZones`).
  - `MathF.Exp` (`Ambience`) and `Dsp.Coef` left inline.
  - `Mathf.SmoothStep` clamps its input and returns `from` when `from ≈ to`: only replaced where `t` was
    already in [0, 1] (`RoadMeshBuilder.Smooth` clamped itself). `DoorLeaf.SetSwing` (unclamped) and
    `ProceduralWorld.SmoothStep` (double, variable edges, linked in tools/BlendCheck) stay.

## Migrating old code / open branches

- Grep your branch: `1f - Mathf.Exp(-`, `Mathf.Wrap(` with `-Mathf.Pi, Mathf.Pi`, `static .* Flat(`,
  `new Vector2(\w+.X, \w+.Z).Length()`, `* (3f - 2f *`. Replace per the rule; add `using UnitSport.Core;`.
- Conflicts on a changed line of the 32 files: keep your logic, write the weight as `MathX.Damp(k, dt)`.
- Not migrated yet (files of open PRs or out of scope, do it when they land): `FootPlayer*.cs` (~30 damping
  weights, `FootPlayer.Crash` smoothstep), `HeavyRig`, `Vehicles/VehicleBody`, `Player/Rideable`, `Items/ThrowAim`,
  `BattleRoyale/BrManager.Client` (damping); `Birds/BirdLife`, `BattleRoyale/BrCrates`, `Items/PvpProbe`
  (private `Flat`); `Player/RaceLine`, `Player/RaceRoute` (public `Flat`, used by `AutoPilot`, `NpcArrival`…),
  `World/RaceNpc.Flat(a, b)` (= `FlatDistance`); `AutoPilot.Wrap`, `NpcArrival.Wrap` (= `WrapAngle`).
  Open PRs at the time: #169, #248, #254, #264, #269, #281.

## How to check

`tools/test.sh unit` (MathXTests), then `tools/test.sh quick` (driving and traffic checks run the converted code).
