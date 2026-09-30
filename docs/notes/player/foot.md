# On foot

- **On foot** (`src/Player/FootPlayer.cs`): WASD + Shift at 1.6 / 4.6 m/s, Space to jump, plus
  two momentum moves — **slide** (Ctrl, run only, launches at 7 m/s, gains speed downhill, ends
  keeping horizontal speed if you Space out of it) and **wall jump** (Space in the air against
  a surface past ~70°, twice per airtime, never twice on the same face). Both launches decay
  back to `RunSpeed` through `AirDrag`, so neither raises the top speed on flat ground; the
  air branch *steers without braking* above running pace, because the ordinary `MoveToward`
  air control kills a launch in half a second and makes both moves pointless. Sliding shrinks
  the capsule to 0.9 m, so it fits where standing does not.
