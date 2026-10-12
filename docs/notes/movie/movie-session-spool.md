# Record / Stop streamed to disk (#782)

- **In memory, a minute:** a session's `Recording` is sized `Math.Max(60, 2 × SpoolSeconds)` (rings, world ring,
  sound ring), whatever it lasts. Every `ReplayRecorder.SpoolSeconds` (30; `--moviespool s` for the checks), when the
  studio opens (`Paused` set: before the window or the sound's break loses anything) and at Stop, `Recording.Piece`
  slices what came since the last piece and hands it to the `SessionSpool`.
  - Players: `ReplayRing.Slice` after the last piece, tagged with a per-player `Serial`.
  - World: `WorldRing.Slice(..., raw: true)` keeps the recorder's ids (`WorldTrack.Ids`) and look indices (no looks).
  - Sound: `SoundRing.Drain`, every sample since the last drain, nothing lost or repeated (a time-based slice drifts).
- **The worker** (`SessionSpool`, pure: no engine, unit tested in `MovieSpoolTests`): pieces are chained as tasks,
  one at a time. Players and world go to a temp file `user://movies/.recording-<guid>.spool` (`MovieFile.WriteTrack`,
  `WriteEntities`). Sound goes straight into a `.wav` in `user://movies/audio/` (header rewritten with the sizes at
  close, the waveform's peaks worked out as it goes); a piece more than 0.1 s off where the `.wav` has got to (a pause, a
  hitch that overflowed the capture) or an hour in starts a new one. A `.wav` up to 10 min gets its beat found there,
  so it is saved with the file. A write error is kept and reported at Stop.
- **The join at Stop** (`Finish`, still on the worker): a player's pieces join into one track while each starts within
  1 s of the last one's end (else a clip of its own on the same lane); a piece's starting values that differ become
  events. The world joins by id: an object alive at a piece's start goes on and its starting entry is skipped (it is
  what it already was). Looks are mapped through a copy of `WorldRecorder.Looks` taken at Stop on the main thread
  (the list keeps growing). The earliest start anchors the timeline (`PlaceGrab`). The temp file is deleted.
- **Back to the main thread:** a `CallDeferred` (runs even when the tree is paused) raises
  `ReplayRecorder.SessionStopped(project, error)`; `MovieSession` saves it and raises `RecordingSaved(name)`. A new
  session cannot start while one is `Saving`.
- Measured: 8 s of the fixture in 4 pieces, 722 KB spooled, joined in 110 ms.
- **Not yet:** a crash mid-session leaves the `.spool` and the `.wav` behind, unrecovered. The studio still loads a
  whole recording into memory to edit it (a long `.wav` included).
