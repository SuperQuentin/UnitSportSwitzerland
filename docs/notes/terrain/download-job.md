# Background downloads: DownloadJob

- **What** (`Terrain/DownloadJob`, #515 phase 4): a region download running on a worker thread while
  the game carries on. Started by the map screen, it runs `MapCore`'s `Planner` steps — the same
  ones the terminal wizard runs — so there is one definition of what downloading a region means.
- **Deliberately static, and deliberately NOT reset with a world.** Almost every static here is
  cleared in `WorldStatics.Reset` (`ui/teardown`); this one must survive, because outliving a world
  is its reason to exist. It holds nothing belonging to a world.
- **Progress** is an immutable `Progress` record the job publishes under a lock and the UI polls
  five times a second — no signal per output line, and no lock for the main thread to wait on.
  `IStepProgress` is the step's end of it.
- **Cancelling is an ordinary end**, not a failure: everything already downloaded or built stays and
  a later run resumes, which `mapsetup.json` already made true. A second job is refused rather than
  queued — two writing the same folders would fight over the shared `.swiss_data_manifest.json`.
- **Tiles do not appear under a player already flying.** The download continues while you play, but
  the result is picked up the next time a world loads, and the screen says so. Re-blending the
  generated fill (`terrain/generated-fill`) at a boundary moving under the camera is its own job.
