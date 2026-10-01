# Skiing with the body (#186)

- **Where the values go.** `XrRig.UpdateSki` writes `XrSession.SkiSteer`, `SkiPole` and `SkiTuck`
  every frame while the local player rides `Skis`, and zero otherwise.
  `FootPlayer.RidePhysics` merges them into the `RideInput` when `RideControls` is null, so
  probes and autopilots keep full control.
- **Lean:** the head's X offset from the recentred pose. ±0.16 m is full steer, added to the
  stick.
- **Tuck:** the head 0.28 m below the recentred height (crouching).
- **Pole push:** a hand at least 0.55 m below the head, sweeping backwards faster than 1.1 m/s.
  It gives 0.25 to 1 throttle, decaying over about 0.5 s (the glide). The ski's pole speed limit
  still caps the skating thrust, so poles help on the flat, not on a descent.
- **Plough:** the left trigger (Brake), as on a pad.
- **Haptics:** one pulse per push, plus a snow buzz that scales with speed.
- **Untested on a headset:** the thresholds are first guesses; tune them on a Quest.
