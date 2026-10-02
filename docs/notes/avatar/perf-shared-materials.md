# Perf: one shared figure material

## Rule
- `HumanMeshBuilder.Material()` returns ONE shared `StandardMaterial3D` (lazy static). Use it for
  every vertex-coloured figure, vehicle, prop or preview mesh; never `new` an identical one.
- **Never modify the returned material** (`AlbedoColor`, `Transparency`, ...): it is every figure
  in the game. A variant gets its own material, built once and kept (as the burnt-vehicle soot
  in `VehicleBody.Char`, the lamps in `TrafficMeshBuilder.LampMaterial()`, the glass in
  `CarRig.GlassMaterial()`).

## Why
It used to return a new identical material per call (22 call sites: every figure, car, crank,
aircraft, preview): one material resource and one more state change per object. Static argument
(allocation removed), PR for #221 (`feat/221-avatar-mesh`).

## Same logic, preserved
- Same properties: vertex colour as albedo, per-pixel, no specular, roughness 1.
- Trap: code that took `Material()` and then tinted it (one car, one figure) now tints everything.
  If you need a tint, `(StandardMaterial3D)HumanMeshBuilder.Material().Duplicate()` once and keep it.

## Migrating old code / open branches
- `grep -rn "HumanMeshBuilder.Material()" src` and check that no line after it writes to the
  material (`\.AlbedoColor =`, `\.Transparency =`, `\.EmissionEnabled`, `SetShaderParameter`).
  If one does, switch that site to a `Duplicate()` kept in a field.
- `BirdMesh.Material` and `Item.Material` already cache the result; leaving them is harmless.
- No signature change: no merge conflict beyond the method body.

## How to check
`grep -n "_material ??=" src/Avatar/HumanMeshBuilder.cs`; a burnt car still goes to soot
(`VehicleBody.Char`), nothing else does.
