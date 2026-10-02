# Building triangles: `b.Tri(t)` and `BuildingTriangles.RoofNormalY`

## Rule
- Read a `Building`'s triangle soup only via `var (a, c, d) = b.Tri(t);`
  (`src/Terrain/BuildingTriangles.cs`, extension in namespace `UnitSport.Terrain.Format`). Never
  hand-unpack `Triangles[o + 3]...` with `o = t * 9` again.
- Wall/roof split: use `BuildingTriangles.RoofNormalY` (|normal.y| >= it is roof). Do not declare a
  local `RoofNormalY = 0.45f`: the renderer, interiors and door logic must agree on what a wall is.

## Why
#221 investigation 3/3, cluster #13: the 4-line unpack was copied 6 times and the constant 3 times.
Pure consolidation; `Tri` returns a value tuple (no allocation), same floats, same order. PR #268.

## Same logic, preserved
- Corner order A, B, C = floats 0-2, 3-5, 6-8: normals from `(B - A).Cross(C - A)` keep their sign
  (outward), so wall-facing and door logic is unchanged.
- The threshold stays 0.45. `src/Birds/TownPerches.cs` still has its own `RoofNormalY = 0.45f`
  (it was in open PR #237 when this landed); switch it to `BuildingTriangles.RoofNormalY` once that merges.

## Migrating old code / open branches
- `grep -rn "Triangles\[o" src/` → replace the `int o = t * 9;` + three `new Vector3(...)` lines by
  `var (a, c, d) = b.Tri(t);` (keep the branch's variable names).
- `grep -rn "RoofNormalY = " src/` → delete the local const, use `BuildingTriangles.RoofNormalY`.
- Files touched: `Interiors/BuildingFootprint.cs`, `Interiors/PlanBox.cs`, `Terrain/BuildingMeshBuilder.cs`,
  `Player/HitboxProbe.cs`. A branch editing those loops conflicts on the unpack lines only: keep the
  branch's loop body, take the `Tri` line.

## How to check
`dotnet build`; building meshes and interiors look the same (the arithmetic is identical).
