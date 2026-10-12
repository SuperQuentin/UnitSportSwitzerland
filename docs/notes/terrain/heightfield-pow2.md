# Ground collision cells: 256² height maps, holes past the data (#812)

- **Rule:** a `HeightMapShape3D` handed to Jolt is square with a power-of-two side (`ChunkNode.CollisionMapSide`, 256):
  the cell's 251 × 251 samples fill its first rows and columns, NaN (a hole, Jolt's "no collision")
  the rest, and the shape is centred on the whole map (`(map - 1) / 2` samples from the cell corner).
- **Why:** Godot builds Jolt height fields with block size 2 and Jolt rounds the sample count up to
  whole blocks. With 251 samples (126 blocks, not a power of two) Jolt 5.5 (Godot 4.7.1 and 4.7.2)
  walks range blocks past the last row in convex-vs-height-field collision
  (`HeightFieldShape::GetEdgeFlags`, from `CharacterBody3D.MoveAndSlide` → `body_test_motion`): a
  parked AN-124 on the ground read past its edge-flag array ~750 000 times a run. Almost always into
  mapped memory (garbage edge flags, no harm); about 1 run in 10 into an unmapped page:
  `Fatal error. 0xC0000005` in `VehicleBody.StepFlyer`, `--an124check`'s old flake.
- **How it was found:** a Godot 4.7.2 mono editor built from source with MinGW and debug symbols
  (`scons platform=windows target=editor use_mingw=yes module_mono_enabled=yes debug_symbols=yes d3d12=no accesskit=no`,
  then `--generate-mono-glue` and `modules/mono/build_scripts/build_assemblies.py`), a vectored
  exception handler printing raw frames (CoreCLR swallows the fault before Godot's crash handler),
  `addr2line` on the `.debugsymbols`, then a bounds print in `GetEdgeFlags`. Under gdb it never
  crashed (60 runs): the debugger changes the heap. After the fix: 0 out-of-range reads.
- Holes cost nothing at runtime; a cell is 256² floats instead of 251² (+4 %).
