# A GPX recording's activity comes from the file, not an assumption

- **A GPX recording's activity comes from the file, not an assumption.** `Runner` always built a
  running figure, so a bike ride played back as someone jogging alongside their own bicycle.
  `GpxParser` now reads the standard `<trk><type>` element (Strava, Garmin and most exporters
  write it; matched by substring - "cycling", "biking", "road biking", "1" all count, since
  exporters do not agree on the string) into `GpxTrack.Kind` (`UnitSport.Player.RideKind`, the
  same enum the player's own mount picker uses), carried through `TrackMatcher` so a road-matched
  copy keeps it. `Runner` builds the real `Cyclist` rig - the one E mounts, not a second one - for
  `RideKind.RoadBike`, via `Cyclist.CreateWithTint` rather than `Cyclist.Create(riderIndex)`:
  the existing factory colours from `HumanPalette.ForRider(index)`'s hue formula, which is a
  *different* colour than the fixed six-entry leaderboard palette `Runner.Tint` already uses for
  a human avatar, and a bike ghost whose rider colour disagreed with its own leaderboard row would
  be its own small bug. Cadence is driven from `Runner.Speed` through the same
  `speed * 60 / 6.2` clamp(40,112) formula `Bicycle.cs` drives the player's own legs from - there
  is no wattage for a recording, but there is a speed, and `Cyclist` already freezes the cranks
  below ~0.01 rpm so a finished or paused ghost simply stops pedalling. Camera mounts (helmet POV,
  ankle cam, etc.) come from `HumanMeshBuilder.MountsForPose(HumanPose.Cycling)`, a fixed-pose
  sibling of the gait-sampled `MountsFor` added for this - a cyclist has no gait phase to sample,
  the legs just turn a crank around a fixed torso. A track with no `<type>`, or an unrecognised
  one, still plays as a runner: this is additive, not a reclassification of every existing GPX.
