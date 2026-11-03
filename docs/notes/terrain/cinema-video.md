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
  (`NeedsLookup`): `SiteFormat(lines)` picks H.264 at most the quality's lines (below) + best sound, prints `is_live` and the duration; a
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
  showing a video has its film music off (`MuteFilmMusic`). The speaker stands 3 m into the hall, not on the screen (there `Hearing`'s rays hit the screen and wall: muffled, #1365), and a hall's audience is silent (`VenuePatrons` sound: an `Auditorium` room never murmurs; it has patrons only while its screen is on): a full hall chattered over the film at +0 dB. Recorded with `--write-movie <wav or avi> -- --fixed-fps --cinemashotcurtains --cinemavideoshot <tone file>,<prefix> --cinemavideolocal` (a 440 Hz tone: its share of the mix says what else plays).
- **Picking**: E / pad Y / VR grip at the booth panel's screen half opens `CinemaPanelUi` (low on the screen,
  the hall's screen above it): Screen off, Cat slides / Stop the video, the cinema's films (open films + the server's list, `FilmCount`/`FilmAt`) in a drop-down with Play the film (#1367, from the start, 2D), then this player's starred links (the star by Stop for a web link playing, `user://cinema_starred.txt`); an admin's **Server's films…** window (name, In the halls' rotation, Add/Save/Remove) sends `/cinema pool add|set|remove` through the chat, the server checks the rights; a link box with Paste and Play, the
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
  `shaders/cinema_screen.gdshader` (`cinema_stereo` before the display options): in a headset `VIEW_INDEX` picks each eye's half; on a flat display red-cyan anaglyph
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
  not a live stream; `add <link> [norotation] [title]`), `set <n> [rotation|norotation] [title]` and `remove <n>`. A film out of the
  halls' rotation (`Film.Rotation`, #1367; protocol 78: `SetPool` carries it) is only picked at the panel: halls draw from
  the rotation alone (`Rotation()`, cached per list version). The panel names a showing by its title. Checked: A, B and C (late) draw the same
  film and loop phase for every hall (`cinemanetcheck`); `--cinemavideoshot showing,<prefix>` (Cosmos Laundromat 604 s in,
  on the clock within 50 ms).
- **Side curtains frame the picture** (the owner's, Oct 28: "open and close to adapt to the streamed ratio", smoothly;
  `InteriorNode.Curtains.cs`): each screen's side folds left its static mesh for a node per side over a mesh built once
  in the interior's frame (`InteriorMeshBuilder.Curtain`; the shader looks room lights up by the mesh's own coordinates,
  so the node is stretched about the curtain's outer edge along the screen, never moved into another room). They frame
  the screen's own picture from the start (a screen narrowed by the room's height is framed at once) and close in over
  a narrower picture's black bars (`FrameCurtains`, from `ShowVideo`; the slides and a stopped video open them again),
  on a critically damped spring (`MoveCurtains`, per frame from the booth, written only while moving: ~3 s, no
  overshoot, smooth when the target changes mid-way). The picture's width is measured from the frames on the feeds'
  workers (`CinemaVideo.ContentWidth`: the lit columns on 9 rows, a 3D frame's left eye), every 5th frame decoded here or
  every 3rd through the relay, kept as the widest seen once 1.5 s of measures came (`CinemaVideo.Shape`: a dark scene
  never narrows it, a black start says nothing): any source, link, relay, share, file or NDI. Big Buck Bunny (16:9):
  75 %, the curtains on its edges about 4 s in (`--cinemashotcurtains --cinemavideoshot <link>,<prefix>`: five shots 1.5 s
  apart, the camera in the seats).
- **The projector's display** (the owner's, Oct 28: "cover/contain/stretch and aspect ratio selection, default contain
  with the full screen ratio, curtains adapt"; `CinemaBooths.Display.cs`): per hall a fit (`CinemaVideo.Fit`: Contain,
  Cover, Stretch) and the ratio of the frame the picture is fitted into, centred at the screen's full height
  (`CinemaVideo.Ratios`: full screen 2.39:1, 2:1, 1.85:1, 16:9, 4:3), packed in one int (`PackDisplay`); 0 = contain at
  the screen's ratio. A hall's setting, like the lights: kept across videos, the same for everyone (`AskDisplayRpc` →
  `ServeDisplay`, inside only → `SetDisplay`, sent to a joiner; protocol 64); the panel's **Display** row, `/cinema fit
  contain|cover|stretch`, `/cinema ratio full|2|1.85|16:9|4:3`. Applied per pixel to the decoded frame, nothing decoded
  again: `shaders/cinema_display.gdshaderinc` (`display_at`, the same as `CinemaVideo.MapScreen`, tested) shared by the
  screen (`cinema_screen.gdshader`, every video now, 3D included; replaces `cinema_stereo`), the port's glass
  (`cinema_port.gdshader`) and the marched beam (`projector_beam.gdshader`): they all show the same framing (defaults =
  the slides' whole frame). The picture's rectangle comes from the frames (`ContentSize`: width and height, the shape
  keeps the widest and the tallest). The curtains frame what is shown (`VisibleWidth`: the frame for Cover and Stretch,
  the picture inside it for Contain). The High-detail projector light carries the frame as decoded, so a display other
  than the default (or a 3D film) keeps the pasted screen at every detail. The slides are not touched. Shots:
  `--cinemadisplay <fit>,<ratio>` with `--cinemavideoshot` (and `--cinemashotcurtains`, `--cinemashotport` for the
  porthole's glass from 2.5 m).
- **Video quality** (#1319, the owner's: "a video quality setting", after asking why 854x358): what this machine decodes
  itself, `CinemaVideo.Quality` / `DecodeSize`: Low 480p sources at 854x358 (the phase-1 size, 37 MB/s of frames), Medium
  720p at 1280x536 (the default, 82 MB/s), High 1080p at 1920x804 (185 MB/s, a 10-frame ring of 6 MB frames).
  Settings › Video › **Cinema video**; **VR cinema video** in the VR rows, High by default (the screen fills much more of
  the view); `CinemaVideoStream.QualityHere`, `--cinemaquality low|medium|high` for the checks. A change restarts the
  hall's decoder at the clock's moment (`PumpVideo`). The relay (480x200, scaled to 854x358) and the live and file shares
  keep their sizes. A picture taller than Low keeps the pasted screen at High detail (the light's would be its atlas's
  blur) and the light gets a nearest-scaled 854-wide copy (`_videoSmall`) for the room and whoever stands in the beam;
  the frozen-light check needs `--cinemaquality low`. The picture's shape ignores a frame lit only in a small part
  both ways (`Shape.Add`: a title card's letters on black were measured as the picture and zoomed to the whole screen).
- **VR sharp cinema screen** (#1319, the owner's: "the movie screen at full resolution in VR, in another layer, like the
  menus"; `InteriorNode.CinemaLayer.cs`, Settings › VR sharp cinema screen, on by default): in a real headset a 2D film's
  screen is an `OpenXRCompositionLayerQuad` (child of the tracking origin, `SortOrder` -3, behind `XrUi`'s -2 and the
  watch's -1) whose `SubViewport` (the frame's size) draws the frame through the hall's display
  (`shaders/cinema_layer.gdshader`, the same `cinema_display` mapping); the compositor samples it at the headset's own
  resolution, where the mesh screen is resampled into the eye picture first. The screen's mesh becomes a hole
  (`cinema_hole.gdshader`: colour and alpha multiplied by 0, depth-tested), so heads, seats, the curtains and hands stay
  in front, as the menus' hole. Placed again every frame (`HoldLayer`, from `CinemaBooths`: the origin moves with the
  player). Not for 3D (a quad layer shows both eyes one picture), `--xrsim`, or a runtime without quad layers
  (`IsNativelySupported`, asked once a run). Untested in a headset when written (TO-TEST-BY-HUMAN).
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
  cinema: `_pasted`, `_thrown`, `_moving`, `_booth`, `_panel`.png; FAILED when `_moving` hardly differs from `_thrown` a second
  earlier: a light copies its projector texture when it is set, so the High light takes two textures in turns), `--cinemanodecode` for the relay (this process its own server).
  Without ffmpeg on PATH (a dev build has no `bin/`), put the release's `test_output/release/tools/<os>` first.
