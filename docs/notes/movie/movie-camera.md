# Movie camera track, studio view, zoom and cuts (#669)

- **`CameraTrack`** (pure, `MovieCameraTests`; on `MovieProject.Camera`, saved in `.usmovie` v3). Keys hold:
  - where: LV95 doubles, so origin shifts never move them;
  - which way: a world quaternion (shifts only translate, so it stays valid);
  - the lens: mm on a 36×24 sensor, `Fov = 2·atan(12 / mm)`, 14–200;
  - the motion to the next key (`KeyEase`):
    - Smooth: a Catmull-Rom path through the neighbouring keys (C1, no corner at a key), rotation slerped and
      the lens eased by smoothstep.
    - Linear: lerp and slerp.
    - Cut: holds the key's view, then jumps at the next key.
  - `LookAt`: an actor lane the camera aims at instead of its rotation, until the next key.
  - `Set` replaces a key within one frame of the playhead (keeping its ease and aim). `Sample` loops instead of
    a lambda: it runs every frame.
  - `Compact` remaps or clears `LookAt` when actor lanes are dropped.
  - `Duration` includes the last key.
- **Studio view** (`StudioCamera`):
  - **Orbit**: the selected actor. Tab, or picking a clip, comes back to it.
  - **Free**: hold the right mouse button on the view with W A S D, Q E (Shift ×6). The keys are read raw,
    because the studio's `UiFocus` holds the movement actions. The position is kept as a `GlobalPos`.
  - **Track**: `ShowPose` draws the camera track's pose, aiming at the head (+1.2 m) of a `LookAt` actor's
    puppet. Any turn, fly or zoom takes it over in Free mode, and `TookOver` switches "Look through camera"
    off, so the user can frame a better view and press I.
  - On a pad: the right stick turns, LB / RB zoom or dolly (the timeline's own zoom while it has focus,
    `PadBusy`).
- **Keys on the timeline:** the Camera row sits on top.
  - Diamonds; a ring means the key aims at an actor.
  - A thick path is Smooth, a thin one Linear, dashed with a step is a Cut.
  - Click a key to pick it (the playhead goes there), drag to retime it (snapping like clips), right-click for
    a `PopupMenu` with ease, aim, "Set from the view" and delete.
  - The same options sit in the key strip under the transport (ease, aim, lens slider, ✕), which pad and VR
    reach by focus. With no key picked, the lens slider sets the view's own lens.
- **Set a key:** I, the ◆ Key button, or pad View/Back (VR: hold Menu, which `XrPad` sends as Back).
- **Zoom:**
  - The wheel over the timeline zooms about the pointer, 0.5–2000 px/s, down to single frames. Shift+wheel
    or a side wheel scrolls.
  - − / Fit / + and = / − zoom about the playhead (about the middle when the playhead is off screen).
  - An `HScrollBar` under the timeline follows `ViewChanged`.
  - The ruler goes to tenths below 1 s a tick (labels cached per tenth).
- **Cuts:**
  - S, pad X: splits the selected clip when the playhead is on it, else cuts all.
  - Shift+S / "Cut all": `MovieProject.CutAll`, every clip under the playhead on every lane.
  - Ctrl+click on a clip: the blade cuts it there, snapped (Ctrl+Shift: unsnapped).
- **Rows wrap** (`HFlowContainer`): at 2560 px with a big UI scale, a fixed row ran off the screen.
- **Not yet** (#637 milestone 2): several cameras with a cut list between them, export through the camera
  track, depth of field and focus pulls, locks such as follow and orbit.
