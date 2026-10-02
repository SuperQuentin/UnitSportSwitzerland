# Cockpit kit: one wheel, dial, lamp, pedal and mirror builder for cars and heavies (#221)

## Rule

- The steering wheel and column, the dials' plane, dial faces, needles, warning lamps, pedals and
  mirrors of a cockpit are built by `Avatar/CockpitKit` (`Wheel`, `Panel` with `DialFace`/`Needle`/`Lamps`,
  `Pedals`, `Mirror`, `Facing`). Never copy one of them into a cabin builder again.
- What differs between vehicles is data: a `CockpitSpec` (column, rim, hub, spokes, mark, dials' plane
  ahead of the hub, pedal arm and pads, mirror rim) kept next to its builder (`CarMeshBuilder.Kit` in
  `CarCabin.cs`, `HeavyCabin.Kit`), plus the per-call dial and lamp arguments. A new kind of cockpit is
  a new `CockpitSpec`, not a new copy.
- Dial ticks are absolute lengths: a car passes `0.013f, 0.007f`, a heavy `0.22f * radius, 0.12f * radius`.
- The shared dial colours (`Dial`, `Tick`, `RedBand`, `Needle`, `Bezel`, `MirrorFace`) live in
  `CockpitKit` only. `Trim` stays per vehicle (a car's is darker), so it is in the spec.

## Why

#221 cluster #5: `CarCabin.BuildCabin` and `HeavyCabin.Build` carried the same ~100 lines with
different constants. After: −180 / +56 lines in the two builders, the kit ~150 lines with its docs.

## Same logic, preserved

- Meshes are byte-identical: a temporary `--cockpitdump` hashed the raw vertex, normal, colour and
  index arrays of every cabin mesh plus the shell (outside mirrors and heavy dashes go into it), pivots,
  axes and mirror records, for all 26 cars and 7 heavy sections: same SHA-256 before and after.
- Same float arithmetic order everywhere (`r - k.RimIn`, `at - normal * depth * 0.5f`, `(i - middle) * pitch`).
- **Trap**: `CarMeshBuilder` is a partial class across files. A `static readonly CockpitSpec Kit = new(Trim, ...)`
  in `CarCabin.cs` initialised before `Trim` (in `CarMeshBuilder.cs`) and took it as black (the dump caught
  it). That is why the car's `Kit` is a lazy property. Never build a static field from another partial's
  static fields.

## Migrating old code / open branches

- Conflict in `CarCabin.cs` / `HeavyCabin.cs` around `// ---- column, wheel ----`, `DialFace`,
  `BuildNeedle`, the lamp loop, the pedals' `Select` or `Facing`/`Mirror`: keep main's kit calls and
  move your changed constant into that vehicle's `Kit` (or a new `CockpitSpec` field if it was not a
  number before). A new dial: `panel.DialFace(...)` + `panel.Needle(...)`.
- `Dial`/`RedBand`/`Tick`/... not found in `CarMeshBuilder` or `HeavyCabin`: use `CockpitKit.X`.
- `CarRig` and `HeavyRig` (the rigs that animate these parts) were not touched.

## How to check

`--cockpitcheck` and `--truckcheck` (`tools/test.sh quick`). For a refactor that must not change a mesh,
hash the arrays before and after like the PR did (the dump code is in the #221 cockpit PR description).
