# A player's process priority returns to what its creator gave it

- `FootPlayer` runs at priority 10 while seated or near a deck (after the vehicle it rides has
  moved) and goes back to `_basePriority` after: the priority it had at `_Ready`, normally 0.
  It used to go back to a hard 0, every frame (`UpdateSeated`), so a priority given by whoever
  made the player was silently lost.
- `--synccheck` relies on that order: owner (0), probe (1), mirror (2). With the mirror reset to
  0 it ran BEFORE the probe, so each comparison set the owner's last frame against the mirror's
  current one. On even frames that is invisible; under machine load one long frame of gait or
  crank made the hand (0.25 m) or the crank (0.25 rad) fail (#279).
- The crank also legitimately trails by the copy frame's turn: the owner publishes its crank
  before its `Cyclist` turns it that frame. The probe's crank allowance is one frame of cadence
  at the longest of this frame, the last one and the copy frame.
- How to check: `--synccheck --world flat` with long frames forced (a temporary
  `OS.DelayMsec(30..160)` on 5 % of the probe's frames) passes; before the fix it failed 6/6.
