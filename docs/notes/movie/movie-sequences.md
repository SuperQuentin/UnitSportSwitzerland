# The Sequences screen (#778)

The editor's second mode tab (Ctrl+2). The model: `movie-shots` (sequences, cuts, transitions).

- **Sequence tabs** above the edits: one per sequence, the active one lit (its cuts are the Program row), `+` for
  another over the whole movie. Right-click a tab: rename, "Starts here" / "Ends here" (the playhead sets
  `Sequence.In` / `Out`), delete (never the last).
- **Shots onto the timeline:** the library stays on the left in this mode too; a row dragged onto the Program row
  (`SetDragForwarding` → `TimelineView._DropData`) makes the movie cut to that shot where it lands, snapped to beats,
  markers and clip edges.
- **Transitions:** right-click a cut: the shot it cuts to, the transition (Cut, Camera blend, Crossfade, Fade to
  black, Wipe, Whip pan, Zoom punch) and its length (0.3, 0.6, 1, 2 s). A transition is drawn as a pale band over
  its span with a letter in a disc on the cut (B, X, F, W, P, Z).
- **Crossfade and wipe draw two pictures** (`MovieStudio.ShowMix`): the player keeps the outgoing shot whole; a second
  viewport (`_previewB`, drawn only during such a transition) films the incoming one and is laid over the player,
  faded in by the mix (crossfade) or uncovered from the left edge (wipe, a clipping control as wide as the mix).
- **Auto-direct** (`AutoDirector`, pure, tested): over the active sequence's stretch, from the picked shot's group (all
  shots when it has none). Cuts on the bars (every 2, 4 or 8 beats, whichever gives 2–4.5 s shots; every 3 s without a
  beat); per stretch the best-scoring shot that can film there: a following rig when its actor moves faster than
  8 m/s, a world shot when slower than 3 m/s or turning, never twice in a row, less of one used just before. The old
  cuts inside the stretch go; the new ones are plain cuts to edit.
- Screenshots: `--moviestudio 13 9 --moviewipe --uishot <png> 17` (a 2 s wipe into a lead shot, the playhead in it,
  a second sequence tab).
