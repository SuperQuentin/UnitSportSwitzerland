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
- **Through the server** (phase 2, `CinemaBooths.Relay.cs`, `CinemaRelayTap.cs`, `CinemaRelayFeed.cs`, protocol 60):
  who: a hall whose panel has **Through the server** on (`/cinema relay on|off`; the switch keeps T0), a client that
  cannot decode (phones, Quest, `--cinemanodecode`), a client whose own decoder failed for that link and T0
  (`_failedHere`: a blocked domain). Such a client says `WatchRelay` every 2 s; the server accepts it only from inside
  that cinema while the hall plays, keeps one `CinemaRelayTap` per watched hall (closed 10 s after the last watcher
  left, or when the link changes) and sends each watcher its frames (`RelayFrame`, unreliable: a lost one is skipped),
  sound (`RelaySound`, reliable) and status on ENet channel 5 (the radio has 3, the concert 4); its own client and an
  offline game directly. The tap: yt-dlp and ffmpeg on the server, JPEG 480x200 @ 15 fps (`-q:v 7`, split on the
  FFD8/FFD9 markers), mono 24 kHz µ-law in 4800-sample chunks on a fixed grid, each stamped with its moment of the
  video and sent up to 2.5 s ahead (a live stream's moments are the server's: a frame 3 s after it came, so everyone
  on the relay is on one clock even live). The feed keeps the small JPEGs as they came and decodes each 0.3 s before it
  is due (into eight pooled images, scaled to 854x358); the first version decoded on arrival and starved its pool
  (frames 1.9 s late). Measured: on the clock at 7/7 marks, worst 26-54 ms, sound due at every mark.
- **One feed for both ways**: `ICinemaFeed` (`CinemaFeed.cs`, with `CinemaLookup`: yt-dlp, ffmpeg arguments, process
  start and stop for the decoder and the tap): `Picture` (an `Image`), `Tint`, `Pump`, `SoundAt`/`Sample`/`Heard`.
- **Phones and Quest**: no ffmpeg: through the server (the panel says so).
- **Live sources** (phase 3, `CinemaBooths.Live.cs`, `CinemaCapture.cs`, `PcSoundLoopback.cs`, `WindowGrab.cs`): the
  panel's **Share from this computer** (desktop only): a monitor, a window or a camera, a quality (Low 480x200 @ 15,
  Sharp 960x400 @ 10 for text, Smooth 960x400 @ 20), **PC sound** and a microphone. `AskLiveRpc` → `ServeLive` (inside,
  the 2 s cooldown) writes the hall's link `live:<peer>:<label>` (`CinemaVideo.LiveLink`; `Allowed` refuses the scheme
  from a player), relay on. The streamer's game captures (`PumpCapture`, wherever the player looks) and pushes
  `PushFrame` (unreliable) / `PushSound` (reliable) on channel 5; the server keeps only the owner's (`Pushed`, at most
  64 waiting, a JPEG at most 512 kB) and forwards them in `PumpRelay` like a tap's; the owner out of that cinema for
  5 s, or anything else played, ends it. Frames are stamped with the hall's moment of capture + 1.5 s
  (`LiveDelay`), the sound runs on the clock as the radio tap's (a source short gives silence, one ahead is trimmed to
  0.3 s), so everyone, the streamer too, sees and hears the same moment.
- **Capture per OS**: monitor gdigrab with the monitor's rectangle from `EnumDisplayMonitors` (Windows), x11grab
  (Linux, X11 only: Wayland not done), avfoundation's "Capture screen N" (macOS); window: `PrintWindow` with
  `PW_RENDERFULLCONTENT` on its handle (GPU-drawn windows, even under the game; not minimised; gdigrab's `title=` gives
  a blank browser, and ffmpeg's `gfxcapture` (Windows.Graphics.Capture) sends a frame only when the window changes and
  ffmpeg held even the first back, measured Oct 28), letterboxed and made a JPEG in C#; camera dshow / v4l2 /
  avfoundation from ffmpeg's `-list_devices` (Windows found the Logi C310 and the Quest headsets' virtual cameras).
  Windows are listed with `EnumWindows` (visible, titled, not tool windows, not cloaked, not this game).
- **PC sound**: Windows WASAPI process loopback (`VAD\Process_Loopback`, `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`
  on this process: every app but the game, so the hall's own sound never echoes; Windows 10 20348+ / 11), COM interop,
  48 kHz stereo 16-bit read on an MTA thread, averaged to 24 kHz mono; Linux PulseAudio's `@DEFAULT_MONITOR@` through
  ffmpeg; macOS: phase 7 (ScreenCaptureKit). Mixed with the microphone sample by sample.
- **A video file** (phase 4, `CinemaFileSource`; owner: streamed from the streamer's game, nothing stored on the server):
  the panel's **Play a video file…** (a native file dialog, `CinemaFileSource.Extensions`) shares `File: <name>` as a live
  source whose frames come from the relay's own tap (`CinemaRelayTap`, now at any `LiveQuality`) run on the streamer's
  machine on the local file: decoded from the hall's moment on the clock, stamped with video time, sent up to 2.5 s ahead,
  restarted further on after a stall, the share stopped when the file ends. `ILiveSource` is what a share pushes from
  (capture or file). The check plays the test film this way after the test pattern.
- **NDI** (phase 5, `CaptureKind.Ndi`): the share's source list adds the network's NDI sources (`ConcertNdi.Sources`,
  1.5 s of looking; none without the runtime): OBS, a vision mixer, the concert's own program camera ("PGM", sent by
  `/concert ndi on`: the in-world cameras). The streamer's game receives it with the concert's `ConcertNdi.Receiver`,
  now at full bandwidth (`full`; a concert screen keeps the preview) and with NDI's audio (`recv_capture_v2`'s audio frame,
  planar floats, interleaved, freed with `recv_free_audio_v2`): the newest frame letterboxed and made a JPEG at the
  share's quality on a worker, the sound to the relay's mono, sent like any share. No PC sound on top (it would double).
  Tested Oct 28 against OBS's NDI output (NDI 6 Tools, `C:\Program Files\NDI\NDI 6 Tools\Runtime`): the source is
  listed as `NDI: <MACHINE> (OBS PGM)`, `--capturecheck` receives 24 frames in 3 s at Sharp and its sound (13 of 15
  chunks not silent), and `--cinemavideoshot live:ndi,<prefix>` shows it on a hall through the server 83-133 ms off the
  clock. NDI Tools also installs "NDI Webcam" virtual cameras and mics: they show in the lists as ordinary cameras.
- **3D** (phase 6, the owner's: anaglyph on flat displays, each eye its own picture in VR): a hall's video carries a
  `CinemaVideo.Stereo` (2D, side by side, top and bottom; protocol 62: `AskVideoRpc`, `AskLiveRpc`, `SetVideo`), picked in
  the panel and guessed from a link's or a file's name (`Guess`: SBS, HSBS, LR, 3D; TB, OU, HOU, ABL as Blender's 3D Big
  Buck Bunny). Every decoder (`CinemaVideoStream`, the relay's tap, a file share) packs both eyes side by side, each
  fitted to the screen on its own (`StereoFit`: crop, a half layout unsqueezed when an eye is narrower than 1.2:1 or
  wider than 2.5:1, letterbox, `hstack`), so a 3D picture is twice as wide (`PackedSize`). The pasted picture is then
  `shaders/cinema_stereo.gdshader`: in a headset `VIEW_INDEX` picks each eye's half; on a flat display red-cyan anaglyph
  with Dubois' matrices, or the left eye alone (**Red-cyan glasses** in the panel, the viewer's own, `user://cinema.cfg`).
  The projector light, the beam and the port take the left eye (a second texture); a 3D hall is never thrown by the light
  at High detail (one picture per light), it keeps its pasted screen. "Proper separation on supported displays" beyond VR:
  Godot 4 has no quad-buffer stereo output, so a 3D TV or monitor would need the whole game rendered side by side, not
  done. Tested: the 3D Big Buck Bunny (`mirrors.mit.edu/kodi/demo-files/BBB/bbb_sunflower_1080p_60fps_stereo_abl.mp4`;
  the 30 fps abl file there is broken, missing atoms; the mirror delivers 10 s of the 60 fps one in 16 s, so it falls
  behind and restarts: a slow mirror, not the decoder) and a synthetic top-and-bottom file through the relay (77 ms off).
- **What a hall shows by itself** (the owner's, Oct 28: "randomly select from available sources, not client local";
  `CinemaBooths.Showings.cs`): a hall whose screen is on and that nobody chose a video for (the 40 % the cinema starts
  with mid-showing, `Default`) plays a film drawn from its seed and showing number (`PickShowing`) among the open films
  (`CinemaVideo.OpenFilms`: Blender's open movies on Blender's YouTube channels, CC-BY, 480p through yt-dlp; append only)
  and the server's own list, the cat slides one entry among them; never a player's file or share. The film loops on the
  shared clock from the hall's own phase (`LoopStart`), so every peer, a joiner too, shows the same frame and starts it
  again together, with nothing sent but the list. `VideoOf` falls back to it for a hall whose link was never set (a
  chosen video, the slides or the screen off from the panel are kept, `""` included); the draw is cached per hall,
  showing and list version (no string per frame). The server's list: `user://cinema_pool.json`, sent on join and on change
  (`SetPool`); `/cinema pool` lists, an admin `add <link>` (title and length looked up on the server, `CinemaLookup.Probe`;
  not a live stream) and `remove <n>`. The panel names a showing by its title. Checked: A, B and C (late) draw the same
  film and loop phase for every hall (`cinemanetcheck`); `--cinemavideoshot showing,<prefix>` (Cosmos Laundromat 604 s in,
  on the clock within 50 ms).
- `--capturecheck <dir>`: lists what can be shared, shares the first monitor and window 3 s each (frames counted, the
  last as JPEG), records the PC's sound 4 s (play something meanwhile). Measured here: monitor 26-27 frames, window
  30, PC sound 98 % of real time. A desktop check, not CI. `--cinemavideoshot live:monitor|window|camera|test,<prefix>`
  shares on the shot's hall (`live:test` needs `--cinemavideolocal`).
- **Sources tested** (Oct 2026): YouTube `aqz-KE-bpKQ` (Big Buck Bunny), `download.blender.org/durian/trailer/
  sintel_trailer-480p.mp4`, `test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` (HLS VOD), ireplay's live HLS. Dead:
  the gtv-videos-bucket sample (403), Akamai's `cph-p2p-msl` live test (404).
- **Checks**: tier 0 `--filter CinemaVideo`; net `tools/cinemanetcheck.sh` (a 60 s test film with a 440 Hz tone
  made with ffmpeg in `%TEMP%`, never under the project; `VIDEO=<link>` for a web one, `VIDEO=none` to skip): A plays
  it, B outside is refused, A, B, C hold the same link and T0; A (`--cinemanodecode`) watches through the server, B
  decodes it, both through the booth's own feed (`CinemaBooths.ProbeWatch`) and within two of their frames of the clock
  with the sound due at every mark (the probe reports the heard sample as a speaker does). C only checks the state: a
  third ffmpeg pair hit the RAM floor on a 16 GB machine. Then a live share: A shares a test pattern with a tone
  (`CaptureKind.Test`, `--cinemavideolocal` only), B walks into the house, both watch it through the server (A its own
  share), on the clock within 0.25 s (live frames come as captured) with the sound due; A stops it and B sees it end. Windowed shots: `--cinemavideoshot <link>,<prefix>` (Town
  cinema: `_pasted`, `_thrown`, `_booth`, `_panel`.png), `--cinemanodecode` for the relay (this process its own server).
  Without ffmpeg on PATH (a dev build has no `bin/`), put the release's `test_output/release/tools/<os>` first.
