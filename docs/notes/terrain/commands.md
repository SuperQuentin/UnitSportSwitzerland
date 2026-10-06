# Commands

- Tunnel collision check: `<godot> --path . -- --probe lv95E,lv95N,seconds`
- Streaming smoothness: `<godot> --path . -- --fly x,y,z,yawDeg,speedMps,seconds [--rings N --horizon km --builds N]`
  — flies straight at that speed and prints the frame-time distribution; exits non-zero on any
  frame over 33 ms. The way to check a loader change, since a hitch never shows in a `--shot`.
- Generated fill check (no Godot): `dotnet run --project tools/BlendCheck -c Release [-- --render]` —
  synthetic real blocks beside the generator, then the real `FallbackChunkSource` +
  `CachingChunkSource` chain: seams generated|real and generated|generated at both resolutions,
  coarse = decimated full, horizon = grid, point path = grid path, no cliff, no trench, seam kink,
  streaks, invalidation, horizon.bin knots = tile knots, the fill switched off. Non-zero exit on
  any failure; `--render` writes before/after hillshades to `test_output/blend/`. In game:
  `--chunks <partial region> --generated on|off`; a server with no terrain: `--server --generated-world`.
- Generated roads through the road network stage (#559, no Godot): `dotnet run --project tools/BlendCheck -c Release
  -- --generated-roads [--villages N --size N --list]`; in game `--generated-roads raw` draws the bare
  segments instead, for a before/after (`generated-roads-roadgen`).
- Road embankments (no Godot): `dotnet run --project tools/BlendCheck -c Release -- --roads DIR
  [--tiles E_N,...] [--render E,N --scale M] [--walls]`; in game `--roadcheck --embankments [--at E,N]`
  (`road-embankments-walls`).
- Sidewalks and tunnels (#119, `sidewalks-tunnels-runtime`): `--roadcheck --sidewalks [--at E,N]
  --traffic 0`, `--roadcheck --floorat E,N --at E,N`, `--roadperf DIR[,label]` (headless cost per
  tile).
