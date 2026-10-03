# House props: taps, instruments, cinema seats (#433)

- **Cinema seats face the screen.** `InteriorGenerator.Piece.FaceTo`: a free-standing piece turns to
  look at the last piece of that type already in its room, and `TryFacing` puts it on the line out of
  the target's front, 1.8–3.5 m away (2.6 m first), the middle first and then 0.35 m steps to either
  side, with a 0.12 m gap so seats make a row. The home cinema's sofa and both armchairs use
  `FaceTo = CinemaScreen`; nothing fits → the old ring search. Facing convention: a piece is
  authored with its front to +Z, `Turns` t turns it t·90° about up (0 +Z, 1 +X, 2 −Z, 3 −X,
  `FrontOf`). Plan v13.
- **What is interactive** (`Interiors/HouseProps`, node `World/HouseProps` on the server and every client):
  - **Taps**: `Sink`, `Bathtub` and the kitchen `Counter` (its sink, toward the right end, `CounterSinkX`).
  - **Instruments**: `Piano`, `Keyboard`, `DrumKit` (`InstrumentOf`).
  - E targets them with `HouseProps.At`: within 1.3 m of a use spot (`UseSpot`: before the sink,
    the bathtub's tap end, the piano's bench, the drum stool) and facing the piece. A counter
    counts only at its sink (0.9 m), and a piano only from its keys, so both are still searched
    (`LootService`) from elsewhere. `TryInteract` asks `HouseProps.TryUse` after the church radio
    and before loot; the prompt chain in `InteriorManager.UpdatePrompt` likewise.
- **Taps** are the open-doors pattern:
  - `AskTap` → the server checks `SpaceOf(sender) == plan` → `SetTap` broadcast. A joining client
    gets the table through `SendTo`. The 1 Hz server tick turns off the taps of houses nobody is in.
  - Every peer draws a running tap as `Tap<i>` under the `InteriorNode`: a thin translucent
    cylinder from the spout (`TapOf`, matching the taps drawn in `InteriorMeshBuilder`) to the
    basin, a splash disc, and a `PropSpeaker` looping `InstrumentSynth.Water`. `ShowTaps` rebuilds
    them when the node or the table changes; nothing holds them across a rebuild.
- **Instruments**:
  - E sits you down: `FootPlayer.PlayAt` holds the body at `SeatOf` (the piano bench, the drum
    stool behind the kit, standing at the keyboard), facing the keys. While held,
    `_PhysicsProcess` returns early (`HoldAtInstrument`), and the owner publishes
    `PoseKind = PoseSeat` (6). Every peer draws `SeatedFigure` with the hip 0.5 m up
    (`ApplySeatFigure`).
  - The drum kit's collision box leaves the stool out (z ≥ −0.28) so the drummer fits.
  - A door, a crash, a ride or leaving the building stands you up (`StopPlaying`).
- **Keys**: `InstrumentUi` takes them in `_Input` and holds `UiFocus`, so nothing walks or opens.
  - Actions `instrument_0..12` are physical A W S E D F T G Y H U J K (tracker layout, C to C), and
    `instrument_octave_down/up` are Z / X. Shift accents a note. Esc or Space stands up.
  - Drums map the home row to kick, snare, hat, open hat, tom, floor tom, crash, ride; the row
    above doubles them.
- **Notes over the network**:
  - `Strike` plays the note at once on the instrument's `PropSpeaker`, then sends `AskNote`
    (unreliable ordered).
  - The server checks the space and drops anything past 40 notes/s per peer, then relays `Note`
    to every other peer whose `SpaceOf` is that plan (and plays it itself when it is a listen host).
  - `NotesHeard` counts them for the probe.
- **Check**: `tools/housepropscheck.sh` (net, generated world). A turns a sink on, B walks in and
  sees the stream; A sits and B sees `PoseSeat`; A plays 8 notes and B must hear at least 5; A
  turns the tap off and B sees it go off. `SHOTS=1` gives screenshots.
