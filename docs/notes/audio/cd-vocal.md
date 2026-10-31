# CD vocal: the singer separated, analysed, lip-synced, lit and mixed (#1153)

- **A CD is ready before its singer (#1405)**: the burn stops after the beat analysis and the Ogg, so the
  CD is listed and plays at once; `CdLibrary.QueueVocal` puts it on the backfill, which separates, writes
  the stems and sends the block to every client (`Added`) ~30 s later (centre fallback, 32 GB Windows).
  `CdAnalysis.IsStale` counts a block with no vocal source as stale. Not on a headless client (#1169).
  Waiting for it at burn cost 30-36 s per CD and timed out `radiocheck`, `uploadcheck`, `auranetcheck`.
- **Separation after the burn** (`Audio/Cd/VocalSeparator.cs`, on the backfill worker):
  - With the model: UVR's MDX-Net `Kim_Vocal_2.onnx` (~64 MB) on the CPU through `Microsoft.ML.OnnxRuntime`
    (pinned 1.30.0). **Never shipped**: the weights carry no clear redistribution licence.
    `tools/deploy/get-vocal-model.sh [user data dir]` fetches it into `user://models/` (sha256 pinned);
    `tools/deploy/remote-setup.sh` runs it on the Linux server (folders made by the server user first).
    Run it by hand on a dev machine or a host that burns personal CDs.
  - STFT exactly as the model was trained (torch): n_fft 7680, hop 1024, periodic Hann, `center=True`
    reflect padding, channels L re, L im, R re, R im, lowest `dim_f` (3072) bins, the three lowest zeroed;
    chunks of hop x 255 samples, n_fft/2 trimmed each side, no overlap (UVR's demix). 7680 is not a power
    of two: a Bluestein transform on `Audio/Fft`, both channels in one complex FFT split by symmetry,
    frames on all cores. The network answers the vocal's spectrogram (not a mask).
  - Speed (12 threads, ONNX on all cores but two): a 160 s song in ~110 s (network ~94 s, transforms ~15 s).
  - Without the model (phones, a host that never fetched it, a model that throws): a centre-panned
    150 Hz..4 kHz mask from left/right likeness. Worse (octave errors 10 % vs 0.7 % of frames), never nothing.
- **Analysis** (`VocalAnalyzer`, pure C#, linked into the tests): 10 ms frames of the 22 kHz vocal: RMS,
  300 Hz..4 kHz flux (syllables), F1 (300..900 Hz) vs F2 (1.5..3 kHz) energy (the vowel), YIN pitch
  (70..1000 Hz, difference by FFT correlation); boiled down to 20 Hz. Gate 8 % of the track's loud vocal.
  Melody: phrases (gaps <= 0.25 s bridged), rises/falls >= 4 semitones over 0.5..8 s, long notes within
  1 semitone for >= 1.2 s, vocal entries after two bars (at the CD's bpm) without voice.
- **`CdAnalysis` v3**: `voc` base64 20 Hz x 4 bytes (level, syllable, vowel 0 oo..255 ee, MIDI pitch,
  0 unvoiced), `vs` the source (`Kim_Vocal_2` or `centre`), `rises`/`falls`/`holds` (start/end pairs),
  `vin`, `st` stems written. A CD analysed with the fallback is stale once the model is here: backfill
  redoes it. Runtime: `CdAnalysisRuntime.SampleVocal`, `MelodyAt` (rise ramps through a run and lets go
  over 1 s), `MelodyMoment.Of` (`ConcertMusic.cs`).
- **`/cd`** (`CdCommand`, `CdLibrary.Command`): `/cd analysis` counts the CDs per analyser version and
  vocal source, with the queue; `/cd reanalyse [stale | all | <id>]` queues them on the backfill (admin
  online; `all`/an id force a current CD, e.g. after the model arrived). Online it acts on the server's
  library; a client's personal CDs are backfilled at its start.
- **Lip sync**: `DanceFace.Sing(ref face, voice, syllable, vowel, amount)` (jaw on the voice and its
  syllables, Wide from the vowel). The concert's lead artist (`ConcertPerformers`, artist 0, centre
  stage) sings at the **listener's** song time (`ConcertRig.HeardSongTime`: the nearest hang's delay),
  through a `FaceAnimator` at `LipEase` 30/s. Dancers (`RadioGroove.Voice/Syllable/Vowel` ->
  `DanceHearing`): a singer personality mouths every line, others the chorus and a mumble.
- **Lights** (`LightDesk`): a rise lifts the washes and beams up to 25 deg and warms them (fall: down,
  cool); a long note or a vocal entry (fading over 4 s) puts the front spots on the singer
  (`OnSinger`). A build or the 2.5 s after a drop own the rig: the melody waits.
- **Debug**: `/concert debug` window: voice and melody lanes, a line with the note, run, long note,
  spot and vocal source; runs (orange up, blue down) and long notes (white) under the song strip.
- **Stems and the voice/music mix**: the burn writes `<id>.voc.ogg` (the vocal, stereo) and
  `<id>.inst.ogg` (the mix minus it) beside the CD (`CdBurner.StemPath`), deleted with it.
  `RadioSpeaker.Voice/Music` (0..1): at 1/1 the CD plays as burnt; otherwise the stems play in one
  `AudioStreamSynchronized` (sample-locked, a level per stream, changed live), fetched lazily over
  `AssetKind.Cd` with `TileId.N` 1 (voice) / 2 (music), cached beside the CD (protocol 48). The CD
  plays on while they arrive; a server without them leaves the CD as burnt.
  - Who sets it, everyone hears it: a world radio's owner (the thrower; anyone for an unowned one),
    `RadioBody.Mix` replicated, `RadioManager.SetMix` checked on the server; the hand's radio and the
    car stereo, `FootPlayer.RadioMix` (the driver's), carried across a throw/pick-up; a parked car
    plays as burnt. Radio panel: Voice and Music faders under the volume, greyed with a reason.
  - Concert: `CueKind.Voice/Music`, `/concert cue voice|music <0..1>`, the desk's two faders (sent on
    a drag's end and on each pad/keyboard step), `ConcertMusic.Mix` on every hang.
- **Checks**: `--beatcheck` (also the model STFT round trip and the centre fallback,
  `VocalSeparator.SelfCheck`); `--vocalstem <audio file>` separates a real song both ways and writes
  WAVs and CSV lanes to `test_output/vocal/`; unit: `VocalAnalyzerTests` (pitch, holds, runs, entries,
  `/cd` parsing), `DanceFaceTests` (lips on the voice).
- **Not done**: movie recordings (`ActorIo`) do not record `RadioMix`; the church radio has no mix; the
  concert desk's faders do not show a mix set by chat.
