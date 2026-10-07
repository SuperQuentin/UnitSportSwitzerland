# CDs: burnt on the server from a YouTube link, beat-analysed in C# (`src/Audio/Cd/`)

- **One library per server**, `CdLibrary` at `World/CdLibrary` on both sides (RPC path). Files under
  `user://cds/<id>.ogg` + `<id>.json` + `library.json`. A CD is a track in this list, **not** an
  inventory item (stacks carry no per-instance data). Clients get the list on connect (`Library`)
  and additions (`Added`); a client's `RequestBurn(url)` goes to the server, offline it burns locally.
- **Personal CDs (#168).** "Just for me" in the radio panel burns on the player's own machine
  (`RequestBurn(url, personal: true)`, same `CdBurner`, so the client needs yt-dlp/ffmpeg too) into
  `user://cds/personal/` with its own `library.json`. Ids are **negative and random** so they never
  collide with the server's (positive) or another player's. `CdLibrary.Find` looks in both lists;
  `CdCache.LocalPath` answers a negative id from the personal list only (in memory, so a second
  client sharing the same `user://` does not pick it up), and never fetches one. Others see
  "someone's own CD" and hear nothing; the server takes the owner's length, capped at 10 min.
- **Burning runs where the library is** (dedicated server, or the offline client), never on a client
  for someone else. `CdBurner`: `yt-dlp` (best audio stream as is, `--print title`/`after_move:filepath`, `--encoding utf-8` read as UTF-8: else Windows prints the console code page and "Véronique" lands as U+FFFD)
  → `ffmpeg` to mono 22.05 kHz s16le on stdout for the analyser → `ffmpeg` to stereo Vorbis q2
  (~80 kbit/s, ≤ 10 min, ~6 MB). Tools from `bin/` next to the exe (shipped in releases), else PATH, like the GPX export's ffmpeg; missing → a status
  line, nothing else. Argument lists, never a shell string: the link is user input. Hosts are
  whitelisted (youtube.com, youtu.be, music.youtube.com); one burn at a time, one per player per
  minute; a local file path is accepted only from this process (`--cdfixture <wav>` for tests).
- **Default CDs (#718).** `CdLibrary.DefaultUrls` (the chess type beat first, then 9 songs): an
  exported build (`OS.HasFeature("template")`, or `--defaultcds` from the editor) burns each link it
  has no CD of yet (`CdInfo.Source = "default:<url>"`) through the fixture queue at start, one at a
  time; a failed one is tried again next start. No audio ships: the songs are not openly licensed.
  Dev runs and checks skip it, offline, so a `--cdfixture` never waits behind a download.
- **Threading.** The burn is a `Task.Run`; progress and the result cross back through
  `ConcurrentQueue`s drained in `_Process`, because an RPC sent off the main thread never arrives.
- **`BeatAnalyzer.Analyse(mono, rate)`** (pure C#, ~0.5 s for 4 min): Hann 1024 / hop 256 spectral
  flux onset envelope → autocorrelation over 60–200 BPM with a log-Gaussian prior at 120 and a
  half/double check preferring 90–150 → parabolic refinement → comb phase search snapped to onsets
  for `BeatOffset` → rule table (bpm, onsets/s, <150 Hz share, centroid, RMS variance) for
  `MusicStyle` (Pop/Rock/Electronic/HipHop/Chill/Folk). Style thresholds were tuned on synthetic
  signals only. `--beatcheck` runs `SelfCheck()` on synthetic clicks (bpm ±2, offset ±30 ms; downbeat
  from an accented kick, kick envelope on the clicks ±1 frame, quiet/loud halves -> one boundary
  ±1 bar, Calm then Peak, JSON and dict round trip).
- **What a CD carries** (`CdInfo`): id, title, duration, bpm, beat offset, style, energy, and
  `Analysis` (#725, JSON `"analysis"`, null on older CDs). Radios and dancers derive everything else
  from `StartedAt` on the shared clock (`docs/notes/net/clock-sync.md`): nothing per frame on the wire.
- **`CdAnalysis`** (`BeatAnalyzer.AnalyseFull`, same STFT pass, ~1.3 s for 4 min): `av` version
  (`CdAnalysis.CurrentVersion`), `env` base64 of 20 Hz frames x 2 bytes (RMS loudness, <150 Hz onset
  "kick", each 0..255 per track), `downbeat` 0..3 (beat phase with the most low onset + band change),
  `sections`/`kinds` (bar features -> z-scored cosine self-similarity -> Foote novelty over 8 bars,
  peaks >= 4 bars apart, snapped to bars; `SectionKind` Calm/Groove/Peak by loudness vs the track's
  bars, Chorus = loud sections alike). RPC dict: `env` as string, `ss`/`sk` packed arrays, keys
  optional. The chess type beat is analysed on its hand-set grid (`CdBurner.Grid`).
- **Backfill**: at start the library queues its CDs (shared where it owns them, and personal) whose
  block is missing or older than `CurrentVersion`, one at a time on a worker: ffmpeg decodes the Ogg
  (`CdBurner.ReanalyseAsync`, stored grid), `.json` + `library.json` rewritten, the server re-sends
  it with `Added` (a listed id is replaced). No ffmpeg: stops quietly. Separate from the burn queue,
  so a `--cdfixture` never waits. Bump `CurrentVersion` when the analyser changes.
- **Runtime** (`CdAnalysisRuntime`, allocation free, env decoded once per block): `Sample(cd, t, out
  level, out kick)` (t = seconds into the file; kick holds the previous frame fading),
  `SectionAt(cd, t, out index)`, `BeatInBar(cd, beatIndex)` (0 = bar start).
- **Playback at runtime**: `AudioStreamOggVorbis.LoadFromFile` on the main thread (header only), on an
  `AudioStreamPlayer3D` in `RadioBody` — the first audio file the game plays; everything else is synthesised.
