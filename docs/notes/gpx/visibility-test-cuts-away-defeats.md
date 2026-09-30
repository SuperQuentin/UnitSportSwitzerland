# A visibility test that CUTS AWAY defeats a dissolve that was built to avoid cutting away

- **A visibility test that CUTS AWAY defeats a dissolve that was built to avoid cutting away.**
  `LockedOff`, `DroneOrbit` and `LowHeroPass` all re-tested `ctx.CanSee` in `StillGood`, so the
  instant anything drifted between the camera and the runner the Director scored the shot
  "broken" and cut to something else — before the sightline-cut shader ever got a frame to
  dissolve it in. The dissolve existed and worked; Absolute Cinema simply never gave it the
  chance, because pre-empting a shot happens the same frame the obstruction appears and a fade
  needs several. `CanSee` still gates `Begin` — a shot never STARTS aimed at a wall — but once
  running these three now trust the dissolve instead of testing sight afresh every frame. This is
  also why "the old Cinematic mode sees through things and Absolute Cinema doesn't" was reported
  as a difference between modes when the shader code was actually identical for both: Cinematic
  never had a competing cut-away trigger to race against, and Cinema's own `StillGood` was
  quietly winning that race every time.
