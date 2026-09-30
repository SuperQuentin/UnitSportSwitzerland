# The video exporter fed the camera TRACK seconds where it wanted SCREEN seconds

- **The video exporter fed the camera TRACK seconds where it wanted SCREEN seconds.**
  `_step` is `clockSpeed / fps`; `ShotContext.Dt` and every easing rate in `PlaybackCamera` are
  screen rates, and `ctx.Follow()` re-applies `ClockSpeed` itself - so the multiplier was counted
  twice and an export paced visibly differently from the preview the player had just set up at the
  same speed. It is `_camera.Step(1.0 / _fps)`; only `_race.StepTo` takes `_step`.
