# Crash ragdoll: thrown through the windscreen (#214)

- **Trigger** (`FootPlayer.RidePhysics` -> `ThrowFromVehicle`, `FootPlayer.Crash.cs`): a vehicle that loses more
  than `ThrowSpeed` (12 m/s, ~43 km/h) against something solid *right now* while the smoothed shortfall is also
  past 3 m/s (so a bump is not a wall). The smoothed shortfall alone (the old `> 9` test) only just reached 9 in a
  56 km/h head-on hit with the 25 m/s² decel cap, and frame timing decided whether anyone was thrown.
- **`_settle` is time, not contact.** It used to count down only while touching something, so it swallowed the
  first second of the first real crash after mounting: nobody was ever thrown. Now it ticks every ride frame.
- **Ragdoll** (`Player/Ragdoll.cs`): the 20 joints of `HumanMeshBuilder` (no skeleton exists) as verlet points,
  1/90 s substeps, 8 constraint passes. Braced blocks (head, shoulders, pelvis), stiff spine/neck, free elbows and
  knees with a minimum fold distance plus a small push toward their bend (straight legs never buckled: the body
  stood upright on the bonnet). Drawn each frame with `HumanMeshBuilder.BuildJoints` (joints pre-flipped, the
  mesh builder turns them a half turn). Starts from the real seated driver (`CarRig.DriverSeat`/`DriverFrame`),
  otherwise the cycling crouch.
- **World contact** by a swept ray per point per substep plus the terrain height as a floor. Three traps hit:
  - friction is per second (`1 - 6·dt` per contact), never a fixed fraction per contact (that glued the feet);
  - a hit point is stopped on its own line of motion, not `hit + normal·r` (on a slope that slid the body
    downhill at 3-4 m/s, friction or not);
  - vehicles are ignored only for `VehicleGrace` (0.25 s sim) so the body can leave the cabin, and then are
    solid again. Excluded for good, the body fell straight through the car it came out of.
- **Rest**: the body's middle staying within 12 cm for 0.6 s (points wedged between a wall and a bonnet twitch
  forever), or 8 s. Then the owner lies down where it rests (`_downRot`), or stands if it rested upright.
- **Slow motion**: the ragdoll's own clock (`SlowMotion(t)`), 1x for 0.12 s, 0.3x until 0.55 s, back to 1x by 1 s,
  identical on every peer. Nothing else slows down (no `Engine.TimeScale` online).
- **Network**: `PoseKind = PoseRagdoll` (3), `Anim` = launch velocity xyz, w = spin + `RagdollMark` (100, so a ride's
  stale Anim never reads as a launch). Every peer simulates its own copy; remotes are steered (2/s, snapped past
  8 m) onto the replicated position, which is the owner's hips (the owner's body is pinned to them). Remotes start
  from the car's driver seat they last drew (`_seenSeat`). `tools/crashnetcheck.sh`: mean gap 0.2 m.
- **In VR** (`BeginVrCrashView`): no flying, zooming or shaking camera (all of them make people sick in a
  headset). The player is stood at a still, level spot 5.5 m beside the body at standing eye height, facing it,
  and watches with their own head: a `CrashEye` camera under a plain `Node3D` (under the player, the rig would take
  it for the player's eye), which `XrRig` adopts as a position-only anchor. It cuts to a new spot (1.2 s apart at
  least) when the body is over 14 m away or out of sight for 0.5 s; every cut, and the return to the player's
  eyes at rest, is behind `XrRig.Blink()` (the vignette shader's `blackout`). Check: `--xrsim --ride car,19,<abs>.png --wall 70`.
- **Crash camera** (owner, flat): a cut to beside the crash, zooming to keep the body ~2 m across the frame,
  then a chase from the side it watches from (rises when the body is out of sight), then a 0.9 s blend back to
  the normal view (`BlendOutCrashCamera`). Shake via `Shaken`.
- **Sound**: `SfxSynth.BoneBreakBank` (2-4 cracks with a knock, a thump, a grinding crunch) and `GlassBank`
  (bang, craze, shard pings), played as `AudioStreamPlayer3D` on every peer from its own ragdoll's impacts
  (> 7 m/s into a surface = a bone, at most `MaxBreaks`, damage on the owner; softer = a thud).
- Not done: the car's windscreen mesh stays whole (only shard particles fly); NPC drivers keep the old flat throw; the VR blink is untested on a real headset (`--xrsim` does not draw it).
- Checks: `--ride car,19,out.png --wall 70 --crashshots 0.4,1.6,4` (see `commands`), `tools/crashnetcheck.sh`.
