# CDs: burnt on the server from a YouTube link, beat-analysed in C# (`src/Audio/Cd/`)

- **One library per server**, `CdLibrary` at `World/CdLibrary` on both sides (RPC path). Files under
  `user://cds/<id>.ogg` + `<id>.json` + `library.json`. A CD is a track in this list, **not** an
  inventory item (stacks carry no per-instance data). Clients get the list on connect (`Library`)
  and additions (`Added`); a client's `RequestBurn(url)` goes to the server, offline it burns locally.
- **Burning runs where the library is** (dedicated server, or the offline client), never on a client
  for someone else. `CdBurner`: `yt-dlp` (best audio stream as is, `--print title`/`after_move:filepath`)
  → `ffmpeg` to mono 22.05 kHz s16le on stdout for the analyser → `ffmpeg` to stereo Vorbis q2
  (~80 kbit/s, ≤ 10 min, ~6 MB). Tools from `bin/` next to the exe (shipped in releases), else PATH, like the GPX export's ffmpeg; missing → a status
  line, nothing else. Argument lists, never a shell string: the link is user input. Hosts are
  whitelisted (youtube.com, youtu.be, music.youtube.com); one burn at a time, one per player per
  minute; a local file path is accepted only from this process (`--cdfixture <wav>` for tests).
- **Threading.** The burn is a `Task.Run`; progress and the result cross back through
  `ConcurrentQueue`s drained in `_Process`, because an RPC sent off the main thread never arrives.
- **`BeatAnalyzer.Analyse(mono, rate)`** (pure C#, ~0.5 s for 4 min): Hann 1024 / hop 256 spectral
  flux onset envelope → autocorrelation over 60–200 BPM with a log-Gaussian prior at 120 and a
  half/double check preferring 90–150 → parabolic refinement → comb phase search snapped to onsets
  for `BeatOffset` → rule table (bpm, onsets/s, <150 Hz share, centroid, RMS variance) for
  `MusicStyle` (Pop/Rock/Electronic/HipHop/Chill/Folk). Style thresholds were tuned on synthetic
  signals only. `--beatcheck` runs `SelfCheck()` on synthetic clicks (bpm ±2, offset ±30 ms).
- **What a CD carries** (`CdInfo`): id, title, duration, bpm, beat offset, style, energy. Radios and
  dancers derive everything else from `StartedAt` on the shared clock (`docs/notes/net/clock-sync.md`).
- **Playback at runtime**: `AudioStreamOggVorbis.LoadFromFile` on the main thread (header only), on an
  `AudioStreamPlayer3D` in `RadioBody` — the first audio file the game plays; everything else is synthesised.
