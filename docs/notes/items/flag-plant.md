# Swiss flag: ghost, plant and pull-up strokes (issue #111)

- `FlagGhost.Aim(player)` (`src/Items/FlagGhost.cs`) is the one raycast that decides what Use does with the
  flag: `Plant` (valid, or invalid with a reason: too steep / too far), `PickUp` (a planted flag) or `None`.
  `ItemController.PlaceOrPickUpFlag` and the ghost both call it, so the preview never lies.
- Ghost: while the flag is held (no Aim needed, Use works without it) a translucent unshaded flag (alpha 0.4,
  flat green / red) stands where it would be planted, turned towards the planter like the real one; a planted
  flag in view gets a yellow halo (same mesh, 1.6 x wide). Hint under the crosshair: `{use_item}: plant` /
  the refusal / `{use_item}: pick up`. Hidden while a stroke runs or the mouse is released.
- Stroke (`ItemController.Stroke`, timed with timers because `HeldItemVisual` does not advance in third
  person): plant = `ViewPose.Raise` 0.45 s, one-shot `ViewPose.Plant` (in 0.12 / hold 0.1 / out 0.3), the
  flag leaves the pack and `RequestPlace` goes out at the stab; pull-up = one-shot Plant 0.25 s, then
  `RequestRemove`. The whole stroke sets replicated `ItemAction = 2`, so every peer poses the
  `ItemArmPose.Plant` arms (both hands on an upright pole, foot near the ground; remote peers ease in over
  ~0.36 s, hence the long raise). No crouch. The flag is taken from the pack at the stab, so swapping item
  during the raise cancels the plant.
- Spawn effect (`FlagFx.Spawned`, from `PlacedObjects.Spawned`): on every peer, when a Flag is added live
  (RPC `Add`, or offline placement), never for the join snapshot or a redraw: Y scale 0.6 to 1 with overshoot
  over 0.2 s, `SfxSynth.Impact` thud at 0.7 pitch, a dirt puff (`CpuParticles3D`). `FlagFx.Spawns` counts
  them for probes.
- Not done: cloth wave. The pole and cloth are one vertex-coloured mesh on the shared material, so a wave
  would need a split mesh and its own shader.
- Check: `GODOT=<exe> tools/plantcheck.sh` (server + A plants and pulls up through the real item path, ghost
  valid then red on a spawned wall; B, joined before, must see exactly one spawn effect). Pictures
  `test_output/plant_a_*.png` (own view) and `plant_b_NN.png` (remote). Runs with `--traffic 0`.
