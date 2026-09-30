# A hand-built basis must be checked for HANDEDNESS, not just direction

- **A hand-built basis must be checked for HANDEDNESS, not just direction.** `right = up × forward`
  and `right = forward × up` differ by a sign, and that sign is the difference between a rotation
  and a **reflection** (determinant −1). Both `PlaybackCamera.Aim` and `Runner.SafeBasis` had the
  operands the wrong way round, so the replay camera rendered the **entire world mirrored** and
  every ghost was mirrored on top of it. It hides extremely well: the −Z column is unaffected, so
  facing still looks right, and terrain is symmetric enough that nothing looks wrong — until you
  follow a route you know and every turn you took comes back the other way. `--shot` never showed
  it, because `ShotRunner` sets `Rotation` as Euler angles instead of building a basis. The rule:
  `right = forward × up`, and if a basis is built by hand, assert `det ≈ +1`.
