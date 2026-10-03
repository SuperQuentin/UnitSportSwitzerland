# Emote wheel: any dance or gesture, any time (#404)

- **Input**: hold `emote_wheel` (B; pad D-pad up) on foot. `Player/EmoteWheel` (a `CanvasLayer` made by
  `ClientWorld` beside the items) opens a ring of ten, first slot at the top, clockwise; the captured
  mouse pushes a virtual stick (`Relative / 90`, as the quick wheel), the right stick aims directly.
  Release picks; a left click picks too. Pages: mouse wheel, Q / E, `ui_left` / `ui_right` (D-pad, left
  stick). A tap (< 0.25 s, nothing aimed) stops the dance, or replays the last emote. Esc closes
  without a change. It registers with `UiFocus` while open, so the body does not walk.
- **Not with the hammer in hand**: its D-pad up turns the piece. Nor mounted, swimming, sliding,
  knocked out or on a zipline/ladder (`FootPlayer.CanEmote`).
- **Wire**: nothing new. `FootPlayer.DanceId` (already replicated) is 0 none, 1 the radio dance,
  `EmoteDanceBase` (2) + the catalog index for an emote. Every peer draws it the same way.
- **Clock**: an emote follows the nearest music's beat when there is some (`RadioManager.NearestMusic`),
  else `FootPlayer.FreeBpm` (120) off `ClockSync.ServerNow`, the shared clock, so remote copies step
  together without a replicated beat. `StepEmote` keeps the emote through its ease-out and crossfades a
  new pick over 0.25 s (`PrevMove`/`MoveBlend`).
- **Stopping**: E stops it (as the radio dance), the wheel's tap, or anything `DanceAllowed` refuses.
  Emoters do not count in the radio dance's crowd (`DancersAround` counts `DanceId == 1`).
- **Catalog**: `HumanMeshBuilder.Emotes.cs`, `EmoteTable` (append only: the index is on the wire),
  `EmotePages` names the pages of `EmotesPerPage`. Moves: `docs/notes/avatar/dance-moves.md`.
- **Checks**: `--emotecheck` (every emote builds, moves, differs, at 32 phases standing and walking);
  `--emotewheelcheck --world fixture [--view third]` drives the wheel with input events (windowed:
  `test_output/emotewheel_*.png`); `tools/emotenetcheck.sh` (two clients: the remote copy dances the
  same emote on the same beat, no music, then rests).
