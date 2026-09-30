# `IsOnWall()` flickers between adjacent physics frames

- **`IsOnWall()` flickers between adjacent physics frames.** Pressed flat against a building
  face, the solver reports contact on roughly every *other* frame, so a wall jump gated on
  same-frame contact silently misses about half of all attempts — it looks like an input bug,
  not a physics one. `FootPlayer` remembers the last qualifying wall normal for 0.18 s
  (`WallCoyoteTime`) and the last jump press for 0.14 s (`JumpBufferTime`), and jumps when
  both are live. Verified: v.y = 4.6 and 5.41 m/s along the wall normal, one frame after press.
