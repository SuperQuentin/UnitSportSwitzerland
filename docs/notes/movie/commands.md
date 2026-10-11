# Movie commands

- `--moviecheck --world flat` (quick tier, `src/Movie/`, `src/Player/FootPlayer.cs`):
  - A player walks, then takes a plane and climbs, while a 15 s ring records a 16 s run, so it wraps.
  - The clip is then replayed through a real puppet at times sought forwards and backwards. The puppet must be within
    1 cm of the recording, on the recorded ride.
  - A split moved away leaves a hole where the puppet must be hidden.
  - Also fails when `ActorIo.Names` drifts from `FootPlayer.OnChangeProperties`. About 20 s of game time.
- `--moviestudio <s> [t]` (any direct world launch): after s seconds, grabs the buffer and opens Pause > Movie
  studio at movie time t. Made for screenshots:
  `-- --world fixture --flycheck plane --moviestudio 13 9 --uishot test_output/studio.png 17`
  (`--flycheck` quits at ~20 s of flight, so open and shoot before that).
- Unit tests: `tools/test.sh unit` (`MovieTests`: ring wrap, sampling, clip edits, file round trip).
