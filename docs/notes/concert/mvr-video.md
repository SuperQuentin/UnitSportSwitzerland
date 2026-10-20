# MVR video on imported screens (#975)

Builds on `mvr-fixture-details.md` (a VideoScreen is our screen fixture) and `mvr-scene-objects.md`
(its geometry is drawn). Code: `src/Concert/Mvr/MvrVideo.cs` (pure: size, sources, Theora sniff),
`RigData.cs` (`vs`/`vn`/`vh`, `CheckVideo`), `ConcertScreenVideo.cs` (the picture), `ConcertNdi.cs`
(`ConcertNdi.Receiver`), `ConcertMvrTool.cs` (the video cache, `/concert videos`), the screen hunks
of `ConcertRig.cs`; tests `tests/.../MvrVideoTests.cs`.

- **What an imported screen shows by default**: the show's own content, exactly as a generated
  screen: the media-server clips its DMX layer picks (`PaintScreen`) and the IMAG camera feed on the
  camera clip. That needed nothing new: an imported screen is a `FixtureKind.Screen` in the patch.
- **Size from geometry.** `MvrFile.ScreenFrame` takes the bounding box of the VideoScreen's
  `Geometries` (Geometry3D files and Symbols, matrices composed; mesh metres, matrix millimetres) in
  its own frame: X across and Z up give the size (scaled by its matrix), the box's -Y face (the
  audience's) is where the picture sits, lifted `ScreenLift` = 1 cm toward the audience so it never
  fights the same geometry drawn as a scene object (#958, `general/zfight`). No readable geometry: the
  old 8 x 4.5 m scaled by the matrix, at its origin.
- **Sources.** `Sources/Source type=File|NDI|CITP|CaptureDevice` (value: a file in the archive, an NDI
  name, ...). The first one the game can show wins (a Theora file in the archive, NDI); else the first
  is kept for the record; every one not shown is noted on import ("the show's content instead"). The
  rig carries `vs` (`VideoSource`: Show, File, Ndi, Citp, CaptureDevice), `vn` (the name, at most 256
  printable characters) and `vh` (a playable file's SHA-256) to every client; `Check` refuses a
  source on a non-screen, an unknown kind, a hash on anything but a file, a hash that is not 64 hex.
- **Formats.** This Godot build decodes only Ogg Theora (`ClassDB` inheriters of `VideoStream`:
  `VideoStreamTheora` alone). `MvrFile.IsTheora` sniffs the Ogg page for Theora's identification
  header; MP4, MOV, WebM, Ogg Vorbis-only are noted as not playable (`ffmpeg -i in -c:v libtheora
  out.ogv`). Files over 64 MB are not read from the archive (`Entry`'s cap).
- **Where the files live.** Never in the rig nor on the network: the importing machine writes each
  playable file to `user://concert/mvr/video/<SHA-256>.ogv` (kept across runs, never pruned). A
  screen plays its file where that hash is cached; another player of a hosted set either runs
  `/concert videos <file.mvr>` (caches only, sends nothing) or sees the show's content (logged once).
  A transfer was left out: the rig's 2 MB budget and the relay are for DMX-sized data, a video is not.
- **Playing.** `ConcertScreenVideo` (one per screen whose source plays here, never headless): a quad
  the screen's size, unshaded, filling it (no letterbox), above the pixel grid and the IMAG feed while
  it has a picture and the screen's dimmer is over 5 % (dimmer and strobe wash scale it). File: a
  `VideoStreamPlayer` (looping, muted) inside a `SubViewport` that never renders, so it never draws on
  the player's screen; its own texture goes on the quad. Paused, and NDI not uploaded, while nobody is
  within 900 m (the rig's `near`).
- **NDI.** `ConcertNdi.Receiver` uses the runtime `ConcertNdi` already loads for sending: a worker
  finds the source by part of its name (case-insensitive), connects at the lowest bandwidth (the
  preview stream, 640x360 from our 1280x720 program; the first frame or two come full size) and swaps
  two buffers; the main thread uploads the newest frame at most 30 times a second. No runtime: logged,
  the show's content. Tested by pointing a screen at our own `/concert ndi on` program ("PGM").
- **Not done.** CITP (MSEX streams need a CITP peer and its codecs) and CaptureDevice (a follow-up
  through Godot's `CameraServer`; no capture device to test with) are kept and noted, not shown. Projectors: an MVR
  `Projection` names a source and `ScaleHandeling` but no target surface, so there is nothing to
  project onto without guessing; noted ("projector projection(s) not shown"), their bodies drawn.
  Export writes screens back as fixtures, without `Sources`.
- **Checks.** Unit `--filter MvrVideo`; net `tools/concertnetcheck.sh` (B gets an NDI source and a
  file's hash on A's imported screens). In game: a synthetic MVR (three walls: Theora file, NDI "PGM",
  none) with `/concert host 5`, `/concert import <it> stage`, `/concert ndi on` and two shots a second
  apart: the Theora wall's frame counter moves, the NDI wall shows the program camera.
