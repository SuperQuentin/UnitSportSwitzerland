# Shots: rigs, shake, beat links, sequences, transitions (#776)

The model only: the Shots screen that edits it is #777, the Sequences screen #778. Plan: `docs/plans/movie-studio.md`.

- **A shot is a `CameraTrack`** with its shot settings: `Group` (library folder), `Rig`, `Target` (actor lane, -1 none),
  `Part` (body 1.0 m, head 1.6 m, ride 0.8 m up), `Smoothing`, `Shake`, `Links`, a saved noise `Seed`.
  The class keeps its old name until the Shots screen renames things.
- **Rigs** (`ShotRig`): *World* (keys are world poses, as before), *Lead* and *Chase* (keys in the target's heading
  frame: `CameraKey.E` = x right, `Alt` = y up, `N` = z back; the rotation is the frame's), *Mount* (the same, in the
  full rotation of the target's puppet, `MovieStage.MountFrame`). With no keys a following rig uses
  `CameraTrack.DefaultPose`: Lead 6 m ahead looking back, Chase 7 m behind 2 m up, Mount just over the target.
  - The yaw is `FootPlayer.NetYaw` (= `Rotation.Y`, facing −Z): `--movieshotcheck` proves the rigs hang where the
    drawn puppet faces.
- **Follow smoothing** (`ShotMath.Follow`): only the **heading** is smoothed, by a critically damped spring
  (the short way round), integrated at 30 Hz from six time constants back: a pure function of time, no state,
  the same frame forwards, backwards and in an export. The position stays rigidly the target's: smoothing it
  (even led by the velocity) threw a chase into a plane that slowed and climbed. On a turn the camera swings round
  late and softly, as a chase car does.
  `ActorTrack.Frame` samples only position and yaw, so the spring can call it hundreds of times a frame.
- **Shake** (`ShotMath.Shake`): presets Handheld 0.7 Hz, Walking 1.9 Hz (more vertical), Engine 17 Hz, Impact 5 Hz;
  strength, speed, turn/move mix, roll. Three octaves of sines at seeded frequencies and phases: replays identically.
- **Beat links** (`BeatLink`, `ShotMath.Beat`): a parameter (lens mm, shake, distance m along the view, height m,
  roll °, orbit ° round the target, fade) in one of the user's three shapes: *Pulse* (raised cosine, peak on the beat),
  *Step* (through `Steps`, one per beat; empty = 0 / amount), *Ramp* (rises to the next beat, drops). `Every` 1/2/4
  beats (4 = the bar's downbeats) or 0 = the markers. **The beats are the movie's** (`MovieProject.CachedBeats`,
  rebuilt only when sound clips change), never the shot's.
- **`MovieProject.ShotPose(i, t, mount, …)`**: keys (or the rig default) → beat links → the rig's frame → shake and
  dolly in the camera frame → lens. Allocation-free per frame (one cached sampler delegate, cached beats).
- **Sequences** (`Sequence`: name, in, out, cuts): `MovieProject.Sequences`, never empty; `Cuts` is the active one's,
  so the timeline's Program row edits the active sequence. Removing a camera fixes every sequence's cuts.
- **Transitions** (`CameraCut.Transition`, `Duration` centred on the cut), in `MovieProject.Program(t, mount)`: Blend
  (pose and lens eased across), WhipPan (a blend that waits then swings in 30 % of the span), FadeBlack (black at the
  cut), ZoomPunch (lens pushed in towards the cut and back after). Crossfade and Wipe report `From`/`To`/`Mix` for
  the engine to mix two pictures (#778); until then they cut at the middle. The studio draws the fade over the
  preview player (`MovieStudio.SetFade`) while looking through.
- **Files:** `.usmovie` v6 adds each camera's shot settings and the sequences (with the active one). A v4/v5 file's
  cut list becomes the sequence "Main" and its cameras World shots.
