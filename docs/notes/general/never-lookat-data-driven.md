# never lookat data driven

- **Never call `LookAt` on data-driven transforms.** A degenerate target makes Godot raise
  an error, and an error raised inside a C# callback can take the whole runtime down
  ("Fatal error. Internal CLR error." with a stack ending in `DebuggingUtils.GetCurrentStackInfo`).
  Build the basis manually and guard the degenerate cases — see `TrackPlayback.SafeBasis`.
