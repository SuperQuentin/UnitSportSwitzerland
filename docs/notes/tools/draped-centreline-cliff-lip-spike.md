# A draped centreline on a cliff lip is not a spike in the data

- **A draped centreline on a cliff lip is not a spike in the data.** swissALTI3D really does
  drop 2483 m -> 2389 m between adjacent 2 m cells, and a path surveyed a metre either side
  of that edge samples both. `LimitGrade` clamps interior vertices to a per-class gradient;
  it cleans the drivable network but wide excursions on alpine footpaths survive it.
