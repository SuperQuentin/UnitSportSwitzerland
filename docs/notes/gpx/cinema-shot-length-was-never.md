# Cinema shot LENGTH was never the reason it cut too fast at high speed

- **Cinema shot LENGTH was never the reason it cut too fast at high speed.** `_target` and `_held`
  were already in screen seconds and already unscaled by the clock. The collapse came from the
  other two triggers: `Imminent`'s lead window widens with the clock, so at 32x it spans 51 track
  seconds while the clock advances 32 per screen second - the window is essentially never empty,
  and the same event re-fired a cut on every frame past `MinSeconds`. Fixed by capping the lead
  (`MaxLeadSeconds`) and by letting an event pull exactly **one** cut (`Director._covered`).
  `Imminent` must still be called unconditionally, never behind a `&&` short-circuit: it is what
  walks `_cursor` past events the clock has left behind, so skipping it parks the cursor on the
  covered event for ever. Second cause: `LockedOff` and `DroneOrbit` guard themselves with fixed
  metre distances that a runner eats in about a second at 32x, where `LowHeroPass` already scaled
  by `ClockSpeed`. Measured on a 4 km track, 40 screen seconds: **1x 7 cuts, 8x 8, 32x 12 -> 9**.
