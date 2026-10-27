# Cinema video: a link on a hall's screen (#1298)

Epic #1298 (phases: 1 a link every client plays itself, 2 the server relay, 3 screen share and webcam,
4 a video file, 5 NDI, 6 3D). Built on `cinemas` (the booth, its panel per hall, the screens' picture,
projector light, beam, port and glow). Code: `src/Interiors/CinemaVideo.cs` (pure: links, sizes, clock;
tier 0 `--filter CinemaVideo`), `CinemaVideoStream.cs` (one client's decoder), `CinemaVideoSpeaker.cs`,
`CinemaBooths.Video.cs` (state, RPCs, `/cinema`), `InteriorNode.CinemaVideo.cs` (the hall's picture),
`CinemaPanelUi.cs`, `CinemaVideoShot.cs`.

- **Same source, each client decodes it.** The server keeps per hall a link and `T0` (`ClockSync.ServerNow`
  when it served Play): `AskVideoRpc` → `ServeVideo` (inside that cinema, `CinemaVideo.Allowed`, one link per
  peer every 2 s) → `SetVideo` to all, and to a joiner in `SendTo`. Protocol 59. Anyone inside the cinema,
  the last one wins (owner's choice). The server never decodes.
- **Decoding** (`CinemaVideoStream`, desktop only: `Platform.CanSpawnProcesses`): this Godot build plays only
  Theora, so the bundled ffmpeg (`BundledTools`) pipes raw RGBA at 854x358 (2.39:1, letterboxed by ffmpeg)
  and 30 fps, and a second ffmpeg 48 kHz stereo PCM, both `-ss` the hall's position plus a lead (2.5 s, +1.5
  through yt-dlp). Workers fill a 10-frame ring and an 16 s sample ring; a full ring blocks the pipe. The main
  thread shows frame k at `start + k/30` against `ServerNow − T0` (late frames dropped, early held). More than
  2 s behind (a stall, a slow start): restart further ahead (`RestartLead`). Measured: 0-30 ms off the clock
  on three peers (`tools/cinemanetcheck.sh`), a frame's phase apart at most.
- **Links**: http(s) only (no `file:`, `concat:`, devices: every client hands it to ffmpeg, as one argument,
  with `-protocol_whitelist`); `--cinemavideolocal` lets the checks play a local file (server and clients).
  A site's page (YouTube, Twitch, Vimeo, Dailymotion) and any `.m3u8` / `.mpd` go through yt-dlp first
  (`NeedsLookup`): `SiteFormat` picks H.264 at most 480p + best sound, prints `is_live` and the duration; a
  YouTube link gives separate video and sound URLs. Each client looks it up itself (the URLs are per client).
- **Live**: from yt-dlp's `is_live` (or ffmpeg's `Duration: N/A` for a plain link): no seek, an HLS playlist from
  `-live_start_index -2`, frames shown a second after the first arrives. Clients are a segment or so apart,
  not on the clock: what phase 2's relay fixes. Tested: `https://ireplay.tv/test/blender.m3u8` (a 24/7 live
  test stream), resolved to 270p.
- **Showing** (`InteriorNode.ShowVideo`): only the hall the camera is in (or the nearest) decodes, one ffmpeg
  pair per client. Its pasted picture, port and lens glow get copies of the slides' materials with the video's
  `ImageTexture` (one `Update` a frame), its marched beam the texture, its plain beam and the hall's wash the
  frame's mean colour (a sparse grid on the worker); at High detail the projector light takes the frame blitted
  into a black square (a second texture, updated only while the light shows). `VideoOwned` keeps the slideshow
  off them. Ended or failed: the hall's slides and music back.
- **Sound**: `CinemaVideoSpeaker` at the screen, spread over the hall's half-diagonal, the music bus, in
  `MusicFocus`; samples pushed by index on the clock as `WebRadioSpeaker` does (50 ms tolerance). Every hall
  showing a video has its film music off (`MuteFilmMusic`).
- **Picking**: E / pad Y / VR grip at the booth panel's screen half opens `CinemaPanelUi` (low on the screen,
  the hall's screen above it): Screen off, Cat slides / Stop the video, a link box with Paste and Play, the
  last six links played (`user://cinema_links.txt`). `/cinema play <link>`, `/cinema stop`, `/cinema`
  (status: position, the frame shown, restarts) in the chat (`CinemaBooths.RunLocal`, before the server).
- **Phones and Quest**: no ffmpeg: the slides (the panel says so); the relay (phase 2) is their way in.
- **Sources tested** (Oct 2026): YouTube `aqz-KE-bpKQ` (Big Buck Bunny), `download.blender.org/durian/trailer/
  sintel_trailer-480p.mp4`, `test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` (HLS VOD), ireplay's live HLS. Dead:
  the gtv-videos-bucket sample (403), Akamai's `cph-p2p-msl` live test (404).
- **Checks**: tier 0 `--filter CinemaVideo`; net `tools/cinemanetcheck.sh` (a 60 s test film made with ffmpeg
  in `%TEMP%`, never under the project; `VIDEO=<link>` for a web one, `VIDEO=none` to skip): A plays it, B
  outside is refused, A, B, C hold the same link and T0, A and B decode and must be within two frames at 7
  marks (C only checks the state: a third ffmpeg pair hit the RAM floor on a 16 GB machine). Windowed shots:
  `--cinemavideoshot <link>,<prefix>` (Town cinema: `_pasted`, `_thrown`, `_booth`, `_panel`.png).
  Without ffmpeg on PATH (a dev build has no `bin/`), put the release's `test_output/release/tools/<os>` first.
