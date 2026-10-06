# Movie sound: songs, game sound, beats and markers (#656)

- **Songs**: the studio's Music… button (native file picker) or a .wav/.ogg/.mp3 dropped on the window
  (`Window.FilesDropped`).
  - `SoundImport` copies the file to `user://movies/audio/<name>-<6 hex>.<ext>`, so a movie survives the original
    moving.
  - It decodes the file a few seconds per frame behind `Modal.Progress`: `AudioDecodeJob` drives
    `AudioStreamPlayback.MixAudio`, which is Godot's own decoder and needs no sound card. Headless works, and there
    is no ffmpeg.
  - Then, on a worker thread, it finds the beat on an ~11 kHz copy (`Pcm.Decimate`) and the waveform peaks
    (50 a second).
  - The song lands at the playhead on the "Music" lane.
- **Beat detection** (`BeatDetector`, pure, `MovieAudioTests`):
  - Positive log-spectral flux of a 1024-point FFT every 128 samples, less a 0.4 s moving mean.
  - Tempo is the autocorrelation peak between 60 and 200 BPM, weighted by a log-normal around 120. That keeps it
    off half and double tempo. The peak is parabolic-refined.
  - The phase is the offset collecting the most onset. Each beat is then pulled onto the strongest onset within
    ±10 % of a period of the last one, so a drifting band is followed.
  - **Timing anchor:** a frame's flux peaks with the onset three quarters through its Hann window (`OnsetLag`).
    Anchored at the centre, every beat came out 27 ms early.
- **Game sound** (`ReplayRecorder`):
  - An `AudioEffectCapture` at the end of the Master bus, after the limiter, drained five times a second into a
    16-bit mono `SoundRing` at half the mix rate (about 2.8 MB a minute).
  - The capture returns a new array each drain: the recorder's one allocation, 5 a second.
  - While the studio is open the sound is the studio's own playback: it is dropped, and the ring is cleared on
    return, because a gap would put everything before it out of step with the clock.
  - A grab writes the slice as a .wav (`SoundImport.FromGame`) on a "Game sound" lane, trimmed to start no earlier
    than the grab's first actor frame.
- **Playback** (`AudioDeck`, a child of `MovieStage`):
  - One `AudioStreamPlayer` per sound clip, on **Master**. The studio's "World sound" toggle mutes the
    Sfx/Player/Music buses (the puppets' engines and steps) without muting the movie. The toggle starts off when
    the movie has game sound, which would otherwise play twice. Closing the studio runs `SfxBus.ApplyVolumes`.
  - Started at `Clip.Local(time)`, re-sought when off by more than 0.1 s × speed, and `PitchScale` follows the
    speed.
  - Silent backwards, while paused, and while the playhead is dragged (`MovieStage.Scrubbing`).
- **Ghost beats and markers** (`MovieProject.Beats` / `Markers`, drawn by `TimelineView`):
  - Every beat inside a sound clip's in..out, where the clip plays it, is a faint line across all lanes. Each
    fourth of the song's own beats (a downbeat) is stronger.
  - Double-clicking a ghost keeps it as a marker (solid, flagged on the ruler, saved). Double-clicking a marker
    drops it.
  - M (keyboard) or R3 (pad, VR) toggles a marker at the playhead, on the nearest beat within 0.25 s. Ctrl+← →
    jumps between markers.
  - The beats are cached against `SoundKey()`, an allocation-free hash of the sound clips, and recomputed only
    when a sound clip changes.
- **Snapping:** a moved clip's start, else its end, and a trimmed edge, snap to the nearest marker, beat or other
  clip's edge within 8 px (`MovieProject.Snap`). Hold Shift to move freely.
- **File:** `.usmovie` v2 adds the audio lanes, the assets (file name, BPM, beats, peaks), a flag per clip and the
  markers. v1 files load silent (`MovieFile.Write(p, s, 1)` is kept for that test).
- **Game sound beats** (#669): `SoundImport.FromGame` runs `BeatDetector` on a worker thread after the grab.
  - It keeps the grid only when `Confidence` (the autocorrelation peak over the mean of all the tempi tried)
    reaches `BeatDetector.Rhythmic` (2.0).
  - Measured confidence: click tracks 12–20, a 124 BPM loop 5.1, game sound with something rhythmic 2.7–3.1.
    White noise, a drone and engine-only flights scored 1.1–1.4, and got a bogus 157 BPM before the gate.
  - Imported songs keep their beat whatever the confidence.
  - `TimelineView.SoundKey` includes the beat count, so the ghosts appear when the worker is done.
- **Not yet:** a song cannot be re-analysed or have its tempo typed in.
  - Sound in an exported video waits for the export milestone (#637).
