# GPS speed must be averaged over a window, never one segment

- **GPS speed must be averaged over a window, never one segment.** A ~1 Hz recording has
  metres of jitter between consecutive fixes, so differencing a single segment reports a
  walk as a run and never settles. `GpxTrack.SpeedWindow` (6 s either side) makes the
  readout match the avatar's real world speed — verified at 5.8 reported vs 5.6 measured.
