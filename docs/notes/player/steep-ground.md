# Walking on mountain ground: facets pass, the ground's slope decides (#883)

- **Symptom:** walking up real mountain ground stuttered. Measured with `--slopewalk` (below) at three
  spots, `main` lost and found the floor 181 times in 17 walks of 8 s (fall pose, landing dip, thump
  each time), with 43 landings, and stopped dead on some 48° slopes with forward held.
- **Cause:** the terrain's 1 m facets tilt with the height data's noise, so a 40° hillside is full of
  53-60° facets. At `FloorMaxAngle` 52° each was a wall: Godot stops a grounded body dead at one, and
  with `FloorBlockOnWall` false it slid part-way up it and off its floor.
- **Fix (`FootPlayer`):**
  - `FloorMaxAngle` = `FacetFloorDeg` (66°): facets pass. `FloorBlockOnWall` true: pushing into a wall
    no longer lifts a grounded body off its floor.
  - `TooSteep`: the floor is too steep to stand on only when the facet AND, on the terrain, the
    ground's slope over ±2 m (`SteepReachM`, the drawn heights) are past `StandMaxDeg` (52°). The
    average alone carried a body standing on flat ground off a cliff's top edge; the facet alone
    is the noise above. Off the terrain (feet over 0.6 m from the drawn ground: a roof, a rock) the
    facet alone, as before.
  - Too steep: no jump, and it carries you down along the facet at up to `SteepSlideMax` (4.5 m/s),
    the input steering only across it. Holding forward into it ping-pongs at its foot, on the floor.
  - Wedged in the air between steep faces (a crevice in the 1 m ground: several contacts, not moving
    down): the built-up fall speed is dropped, so no landing is owed on the way out.
  - The void rescue and the placement pass read the lowest drawn ground under the footprint
    (`LowestGroundUnder`), not the centre's: at a cliff's foot the centre stands over the wall's
    1 m quad, metres above the feet.
- **After:** 18 floor losses in the same 17 walks, 3 landings, all sliding down ground too steep to
  stand on. `--voidcheck`, `--mantlecheck` pass.
- **Check:** `<godot> --headless --path . -- --chunks <real terrain> --slopewalk --at E,N [--trace]`
  (`SlopeWalkProbe`): finds 25-78° slopes near the point, sprints a fresh body up each, prints
  distance, climb, floor losses, landings and the feet's gap to the drawn ground. Needs real
  terrain for the real numbers; the quick tier runs it on the generated relief (`--systems
  terrain,generated`, ~40 s). Spots used: Dents du Midi `2563500,1112500`, Diablerets
  `2581500,1127500`, the Rhône wall `2578500,1113500`.
- **Feet on the slope:** the capsule's round bottom stands the body r(1/cos - 1) over a slope (0.13 m
  at 40°, 0.2 m at 50°). `ApplyFootPose` lowers the drawn figure by that (`SlopeDrop`: the drawn
  ground's slope over ±0.5 m, so every peer finds the same from the replicated position; only on the
  terrain, standing or sliding, eased). `--slopewalk` reports the figure's gap: at most 0.08-0.15 m
  on the Rhône wall's slopes where the body stood 0.3-0.4 m over them.
