# A map-matched track needs its DISPLACEMENT rate-limited, not its position

- **A map-matched track needs its DISPLACEMENT rate-limited, not its position.** Where the model
  changes road, the projection jumps: the two roads meet at a junction but the switch happens
  wherever the fixes stop being nearer one than the other, which is somewhere else. Measured 29
  steps over 10 m in a single fix across 16 km — one visible sideways twitch every ~550 m. Two
  fixes failed first: switching at the thinning-sample boundary made it *worse* (51), and
  bridging unmatched gaps changed nothing. Slew-limiting the snap offset to 1.2 m per fix took it
  to **0**, and costs only that the track is briefly between two roads at a junction instead of
  exactly on one — which looks like cutting a corner, i.e. like a runner.
