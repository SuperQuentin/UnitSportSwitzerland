# Movie commands

- `--moviecheck --world flat` (quick tier, `src/Movie/`, `src/Player/FootPlayer.cs`):
  - A player walks, then takes a plane and climbs, while a 15 s ring records a 16 s run, so it wraps.
  - The clip is then replayed through a real puppet at times sought forwards and backwards. The puppet must be within
    1 cm of the recording, on the recorded ride.
  - A split moved away leaves a hole where the puppet must be hidden.
  - Also fails when `ActorIo.Names` drifts from `FootPlayer.OnChangeProperties`.
  - Then the sound (#656): a 120 BPM click track is written, imported for real (copy, Godot decode, beats: 120 BPM
    and 16 beats expected) and laid on the timeline, where it must sound playing forwards and be silent
    backwards. The files are deleted after. About 25 s of game time.
- `--moviestudio <s> [t]` (any direct world launch): after s seconds, grabs the buffer and opens Pause > Movie
  studio at movie time t. `--moviesong <file>` also imports a song there. Made for screenshots:
  `-- --world fixture --flycheck plane --moviestudio 13 9 --uishot test_output/studio.png 17`
  (`--flycheck` quits at ~20 s of flight, so open and shoot before that).
- Unit tests: `tools/test.sh unit` (`MovieTests`: ring wrap, sampling, clip edits, file round trip; `MovieAudioTests`:
  beats of click tracks at 95/120/140 BPM within 25 ms, sound ring, markers, snapping, file v2 and v1).
