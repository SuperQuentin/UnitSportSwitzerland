# Raw GPX motion looks like a boat

- **Raw GPX motion looks like a boat.** Three separate causes, all handled: positions are
  smoothed at parse over a *distance* window (`GpxParser.SmoothingWindowM`, so dense 1 Hz
  tracks are filtered while sparse ones are untouched); facing comes from a +/-2.5 s
  look-ahead and is slerped (`Runner.HeadingLookahead`); and the rendered position eases
  toward the sample (`Runner.PositionFollow`), which also hides the 2 m heightfield
  stepping underneath. Seeking snaps rather than easing, so scrubbing stays responsive.
