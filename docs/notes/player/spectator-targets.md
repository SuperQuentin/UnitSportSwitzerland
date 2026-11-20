# The spectator's target cue (#1850)

Under `--frame earth`, the spectator can aim at a body and see how to get there (`docs/plans/planets.md` sections 8, 11).

- **Controls** (`PlayerInput.SpectatorTarget`), the same on three devices: a tap steps to the next body, a hold
  (0.35 s) turns the view to it at a quarter turn a second (`SpectatorCamera.TurnToward`).
  - keyboard: `]` (the riders' power key, ride-only elsewhere);
  - pad: D-pad → (the next item's on foot; free while spectating: no body, no item);
  - VR: the right stick down (`XrPad` sends D-pad → while spectating, the shoulders' layout);
  - a phone: the hint pill (the prompt row "Target" / "Next target").
- **The list** (`Player/TargetList`, plain C#, tested): Earth, Moon, Mars, the Sun, Mercury, Venus, Jupiter and its
  moons, Saturn and Titan, Uranus, Neptune; the body you are on is skipped; after the last, none.
- **The cue** (`Player/SpectatorTargets`, a CanvasLayer): an amber ring on the target, or an arrow at the screen's
  edge toward it, with its name, the distance to its surface and the trip's time at full speed by the spectator's own
  rule (`TargetList.TripSeconds`: ~30 s to the Moon from 400 km, ~14 min to Mars). Text set twice a second; the ring
  drawn without an allocation. Nothing on screen without a target.
- **Check**: `--frame earth --chunks fixture:straight --targetcheck` (headless 20 s): ] and the pad's D-pad →, the hold
  onto Mars within a degree, the list's wrap; windowed it saves `test_output/target/target_moon.png`, `target_mars.png`.
- **Later**: players on other bodies from the far-marker list (C3b); the shuttle's own target and align (C3a, the
  same list).
