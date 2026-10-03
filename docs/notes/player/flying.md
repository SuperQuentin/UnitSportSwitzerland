# Flying

- **Flying** (`Player/Flight.cs`, meshes in `Avatar/AircraftMeshBuilder`): a `Flyer` is a
  `Rideable` whose `Step` is unused — it owns a full 3D velocity and attitude (`FlightMotion`),
  because a ground vehicle is a speed along a heading and none of climbing, diving or banking fits
  that. `FootPlayer.FlyPhysics` carries the velocity through `MoveAndSlide`, turns anything the
  world took off past `CrashSpeed` into a crash (on foot, dazed 1.5 s — not a respawn), and poses
  the visual from the attitude about `Flyer.Pivot` while the capsule stays upright and yaw-only.
  `RideKind` 3–7 appended (never reordered: replicated as an int).
  - **Base jump**: not a mount. On foot, Jump while falling (vy < −3) with > 12 m under you →
    **wingsuit** (lift/drag polar, point mass; a fall pulls out into a glide on its own). A bare
    polar porpoises for ever (measured −36 m/s dive → 13:1 zoom → repeat), so sink is damped toward
    the polar's steady glide: settles at **133 km/h, 2.7:1, 13 m/s sink**. **Look to fly**: `LookSteers`, the
    mouse/right-stick yaw is the heading (banks toward it, 45° off = full bank), look pitch sets the
    lift (−0.35 rad trim, −0.9 dive, +0.05 flare), the camera is the look; the stick still overrides.
    Lift × 1/cos(bank) so turns do not sink. Jump again → **parachute**
    (glide 2.1, 4.2 m/s sink, opening shock from 145 to 36 km/h in 1 s); touching ground → on foot.
    Wingsuit touching ground over 12 m/s = SPLAT. Proximity (< 20 m AGL at > 30 m/s) is scored.
  - **Look banks the suit** (`Flyer.LookBank`, wingsuit and canopies 0.6, in the air only): the free
    look (mouse, right stick) adds `-lookYaw/0.8 × 0.6` to the stick's X. On these craft the look recentres at
    1.2 rad/s at once (every other chase camera, ride or craft, waits `ChaseRecentreDelay` 5 s without look input), so a flick is a nudge and a held look a gentle turn (BrProbe: 11° in 1.5 s).
  - **Carried** (`FootPlayer.Carrier`, the BR cargo plane): no physics, collision off, hidden;
    `Leap(at, velocity, ride)` lets go. It takes one still `MoveAndSlide` first: the body still
    thought it stood on the floor it boarded from, and the wingsuit "landed" at 3 km (SPLAT).
  - **Paraglider** (picker): the same `Canopy` model at 9.1:1 / 38 km/h; on the ground push forward
    to run, Jump to launch; stays worn after landing.
  - **Helicopter** (picker): the look sets the heading (`LookSteers`, mouse/right stick turn
    `_viewYaw`, not `_lookYaw`), stick flies, Space/RT up, Ctrl/LT down, release holds altitude.
  - **Plane** (picker): throttle is a LEVER (Shift/RT up, Ctrl/LT down) — nobody holds a key for a
    whole flight. Stick pitches/rolls, heading follows bank (coordinated turn), roll AND pitch
    self-level hands-off. Thrust 4.5 m/s² — at 11 it beat gravity and a pull-up climbed vertically
    for ever. **Airspeed is carried as state** (`FlightMotion.Airspeed`): re-deriving it as
    velocity·nose fed the stall sink back in as speed once the nose dropped (112 → 394 km/h in 2 s).
  - Check any of them: `<godot> --path . -- --flycheck wingsuit|glide|paraglider|heli|plane[,out.png]
    [--at E,N]` — scripted sortie with the real input actions, speed/sink/glide/AGL every second,
    non-zero exit on a crash or ending under the terrain. `FootPlayer.DebugLaunch` puts a craft in
    the air for it (no runway or launch slope needed to test a flight model).
  - Sound: synthesised helicopter rotor (4.5 Hz blade "whop") and piston engine loops, pitch by
    spool/throttle. No wingsuit/canopy wind — see the removed wind loop above.
