# Church radio and the rat dance (#370)

- **The CD.** `assets/audio/chess_type_beat.ogg` (LFS, not openly licensed: `assets/ASSETS.md`) is
  shipped. `CdLibrary.EnsureRatBeat` (server, or offline game) copies it out of the pck to
  `user://cds/_bundled/Chess Type Beat.ogg` and burns it through the fixture queue (ffmpeg + the
  beat analyser) once; `CdInfo.Source = "bundled:chess_type_beat"` marks it, and every peer knows
  `CdLibrary.RatBeatId` from the library list. `CdLibrary.IsRatBeat(cd)`.
- **The radio.** `FurnitureType.ChurchRadio` (a boombox on a stand) beside the pastor rat
  (`InteriorGenerator.RadioByRat`, both church paths; `InteriorLayout` v12). What plays is
  `Interiors/ChurchRadios` (`World/ChurchRadios`): a server table plan key -> `RadioPlay`, the open
  doors' pattern (`Ask*` RPCs checked against `InteriorManager.SpaceOf(sender)`, `Set` broadcast,
  `SendTo` on join). No entry = silent, the beat "loaded", mode repeat; the 1 Hz server tick
  advances by `RadioQueue` and silences churches nobody is in. Sound: one `RadioSpeaker` at the
  radio of the church this client is in. E at it (`ChurchRadios.TryOpen`, before loot) opens
  `RadioUi` on `Target.Church`; Play with nothing chosen plays the beat.
- **Figures.** The rat and the front-pew people are no longer in the static interior mesh:
  `InteriorMeshBuilder.Build` returns `MeshData.Figures` (parts about pivots, `RatParts` /
  `PersonParts`, a congregant's standing legs hidden), and `ChurchStage` (child of the
  `InteriorNode`) makes a node per part. At rest they draw exactly the old boxes.
- **One rule.** `ChurchRadios.RatBeatPlaying(plan)`: on, `ChurchStage` poses everything from the
  play's start and `ClockSync.ServerNow` (nothing else replicated); off (stop, another CD, the end),
  `Stop()` puts every part back, hides the lights, resets the shader and the camera **in that
  frame**.
- **Intro** (`ChurchStage.Intro.cs`): five hits before the trumpet at `IntroEnd` 2.0 s (`HitTime`: the
  CD's first five beats if they fit, else every 0.4 s). The rat snaps to a pose per hit; every
  player in the church (`InteriorManager.Current`, not in VR) whose client sees the play start
  inside the intro gets a temporary `Camera3D` that cuts per hit, `UiFocus` held, `RadioUi`
  closed; own camera back on the trumpet. Joining later skips it.
- **Dance**: the rat swings a beat each way (body roll/yaw, head against it, arms pumping, a full
  turn every 8th bar); people stand on the trumpet and dance one of five variants by hash, with
  hashed amplitude and phase; every 4th bar they all copy the rat.
- **Night club** (`ChurchStage.Disco.cs`): `ps1_interior` `instance uniform disco` / `disco_center`
  on the room and every part mesh, global `world_disco_beat` written per frame while on: the room
  at ~20 %, rotating hue beams and mirror-ball sparkles. A ball, additive beam cones, four
  sweeping `SpotLight3D`s and a pulsing `OmniLight3D` for characters; `World.DayNight.Disco` dims the
  indoor ambient for the local player inside.
- **Players.** `RadioBody.BeatOf` reports `MusicStyle.RatDance` (never analysed) for the beat, so
  every radio (world, hand, car, church) dances the `RatDance` row: `RatSwing`, `RatArmPump`,
  `RatHeadBob`, `RatHop`; the crowd slot is `RatSwing`. `NearestMusic` includes the church speaker,
  and dancing indoors is allowed to the beat (`FootPlayer.RatBeatHere`, `DanceAllowed`).
- **Check:** `--churchstagecheck[,shot.png]` (`ChurchStageProbe`): a hand-made church, the beat
  burnt, played, five cuts, own camera back, everyone up and varied, disco on; stopped: parts,
  lights, shader and camera back in one frame; windowed, `_rest` vs `_stopped` pixel diff.
