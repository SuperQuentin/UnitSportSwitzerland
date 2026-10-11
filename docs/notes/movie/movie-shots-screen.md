# The Shots screen (#777)

The editor's first mode tab (Ctrl+1; Sequences is Ctrl+2). The model it edits: `movie-shots`.

- **Layout:** the library on the left (`ShotLibrary`), the preview player in the middle looking through the picked
  shot, its settings on the right (`ShotInspector`). The Sequences mode's camera tools and timeline are hidden; the
  transport stays.
- **Library:** compact rows (a 56×32 still, the name, the rig: World in lilac, a following rig in green) under their
  group's heading, shots without a group under "Shots". Clicking (or focusing, for a pad) picks it. Hovering shows a
  384×216 live picture beside the list. Rebuilt only when a name, group, rig or target changes.
  - `+ Shot` (N): a new shot from the view at the playhead, in the picked shot's group. `+ Group`: the picked shot into
    a new group (a prompt).
- **Stills** (`ShotThumbs`): one viewport draws a still only when asked (`UpdateMode.Once`), one shot at a time, kept
  as an `ImageTexture`; all made again when the playhead rests 0.4 s somewhere new or a shot changes. The hover
  picture is a second viewport drawing only while a row is hovered.
- **Inspector**, built again when the pick or the rig changes: name, group, rig (four toggles), follows (actor) and
  part, distance / height / side and smoothing for a following rig (they edit the key at the playhead, or the rig's
  default made a key), lens and aim, Frame from view (G) and Add move (I), the shake style and its four sliders,
  the beat links (parameter, shape, beats or markers, amount, or for a Step its values typed "0 26", invariant
  numbers whatever the locale).
  - Changing between World and a following rig clears the keys: world poses and frame offsets mean different things.
- **Frame from view** keeps the view in the target's frame (`MovieProject.FrameFromView`): a placement's single key
  is replaced, a move gets a key at the playhead; Add move always adds one. Looking through comes back on after.
- **Pads and VR** reach every control by focus (the rows are buttons, the sliders take the D-pad); the keys N and G
  are keyboard shortcuts on top.
- Screenshots: `--moviestudio 13 9 --movieshots --uishot <png> 17` (a chase with engine shake and a lens beat link and
  a lead in "Take-off", a world shot in "Landing").
