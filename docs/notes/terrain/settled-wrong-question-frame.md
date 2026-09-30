# `Settled` is the wrong question for a frame

- **`Settled` is the wrong question for a frame.** It asks "is anything, anywhere, still loading".
  `SettledNear(eye, rings)` asks what a frame actually needs, and distance makes the difference
  invisible rather than merely acceptable: `fog_color` and the environment background are the
  **same colour** and `fog_end` is 8 km, so a tile missing past ring 8 renders as exactly the
  colour it would have had. It must test the *desired* set, not the loaded one — `EvaluateRings`
  breaks out of its loop at the build cap, so tiles further down the nearest-first order have no
  state at all, and checking only the states that exist reports a world as settled before most of
  it has been asked for.
