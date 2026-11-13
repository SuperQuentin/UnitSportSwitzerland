# Back into the body after a long spectator flight (#1811)

- **What the user saw**: after a flight round the Earth under `--frame earth` (thousands of turning re-anchors, several
  3 km / Space regime switches), T back into the body drew only a dark sky gradient and a blurry sun, the light culler
  failing every frame (`create_frustum_points`, `rendering_light_culler.cpp:682`). The body stood still at 1,076 m with
  the wingsuit prompt up (falling, never landing), and it stayed so after a creative flight to space and a `/tp` home;
  the spectator camera drew fine all along. Both times the body stepped in right after the Space → 3 km regime switch:
  T pressed above 50 km waits in `EnterFootWhenGrounded` for the ground under the camera, known only once back in
  the 3 km regime.
- **Why it latched**: the body's camera position adds eased states that ease toward themselves: the landing spring
  (`_landingDip`/`_landingVelocity`, kicked by `_fallSpeed`), the arm (`_armBlend`), the zoom. One NaN in any (a
  non-finite velocity spreads into the fall speed, then the spring at the landing) never leaves it: a NaN eye with a
  sane rotation, so the sky draws and nothing else, and nothing resets them, not a teleport nor creative flight. The
  exact first NaN of the user's session was not reproduced (headless and windowed runs on the leaves, 366 turning
  re-anchors, T in Space: no frustum error); every layer is guarded instead, each tested.
- **The body parked far away**: frozen (`SetBodyParked`) where the camera left it, it is still moved by every shift,
  turned with the world: 8,000 km away its basis is tilted by the Earth's curve between there and here (~60°,
  Switzerland seen from Siberia). The switch used to set only its position: the capsule stood at 60° in the ground.
- **The fix**:
  - `FootPlayer.StepInFromSpectator(at, yaw)` (`ClientWorld.EnterFootMode`): upright at the spot, at rest, facing
    where the fly camera looked, its fall, landing spring and camera easing reset (`ResetViewState`), its kinematic
    collision teleported;
  - `FootPlayer.KeepFinite` (local body, each physics step and each frame, `FootPlayer.Sane.cs`): a non-finite
    velocity, fall speed, landing spring, arm, zoom, view angle, punch, field of view or camera transform is put back
    and named once in the log (`[player] ... a non-finite <what> was put back (#1811)`);
  - `Styles/CameraGlobal.KeepSane` (the drawn camera, before each frame): back to its last finite transform,
    orthonormalised, a near plane over zero and a far plane past it, logged once (`[camera] ... could not be drawn`);
    the raised near plane (#1497) belongs to the camera it was raised on (`_raisedOn`): the switch used to put the
    spectator's base onto the body's camera and leave the spectator's raised.
- **What `create_frustum_points` means**: Godot builds the frustum's planes from the projection in floats; past a far
  plane ~1.7e7 times the near one, (far + near) / (far − near) rounds to 1, the far plane's normal is zero and the
  three-plane intersection fails, every frame. A NaN does not trip it (NaN passes the zero test): a NaN camera draws
  nothing silently. The user's rotated log (`godot2026-11-13T17.39.15.log`) shows the spectator failing too, after a
  `/tp` to Nyon, and every camera fine in Space: a near plane left far too near, with the far plane at most 160 km low
  down. One path found: `Climb` could take a 5 mm door lens's near (`DoorPortals.DoorwayNear`, lowered for a frame)
  as its base and later write it onto whichever camera was drawn. Now the base belongs to its camera, and `Climb`
  never lets far / near pass `CameraGlobal.MaxFarOverNear` (1e7), lowering it back with the rest of its raise.
- **Checks**: `--spacelandcheck` (`Core/SpaceLandingCheck`): body on the ground, the spectator up to 1,500 km, 8,000 km
  east (211 turning re-anchors), four regime switches, 900 km low (3 km re-anchors), T at 70 km, down until the body
  takes over; asserts it upright, its camera finite and orthonormal with sane planes, standing on the ground, then
  poisons the view (`DebugPoisonView`) and asserts it put right; drawn (not headless), it also sets a 1 mm near plane
  and asserts far / near brought within `MaxFarOverNear`. Far away it needs ground there: the real leaves
  (`--frame earth --leaves .../terrain_chunks_cube --chunks ... --at 2500956,1117569`), windowed to run `CameraGlobal`
  (headless draws nothing, so `FramePreDraw` never fires). `--spacelandcheck home` flies back over the start before
  landing: the fixture world (`--chunks fixture:straight`), quick tier.
