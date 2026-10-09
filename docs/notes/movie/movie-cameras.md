# Several cameras and the program (#675)

- **Cameras:** `MovieProject.Cameras` holds `CameraTrack`s, never fewer than one. `Camera` is `Cameras[0]`, the only
  one a v3 file had.
  - Each camera has a `Name` ("Cam N", the first free N, or what the user renamed it to) and its own keys
    (`movie-camera`).
  - `RemoveCamera` drops its cuts, shifts the later cameras' cuts down, and refuses to remove the last camera.
- **The program:** `MovieProject.Cuts`, sorted (`CameraCut`: from `T` on, the movie shows camera `Camera`).
  - `ProgramCamera(t)` is the last cut's camera, or the first camera before any cut. It is a loop, because it runs
    every frame.
  - `CutTo` changes a cut within a frame instead of doubling it.
  - `Duration` includes the cuts.
  - `.usmovie` v4 holds the names, keys and cuts. v3 files load into Cam 1.
- **Timeline rows:** Program, then one per camera, then the actors and the sounds.
  - The Program row draws each stretch in its camera's colour (`TimelineView.CameraColor`) with the camera's name,
    and a tick at every cut. Click a cut to pick it (the playhead goes there), drag to retime it (it snaps), and
    right-click to switch camera or delete it. Del deletes the picked cut.
  - A camera's row draws its keys in its colour. Its name, ▸ when picked, picks it on a click; a right-click
    renames it (`Modal.Prompt`) or deletes it (`Modal.Confirm`).
  - Picking a key picks its camera.
  - `CameraShape()`, an allocation-free hash of every key and cut, triggers the redraw.
- **The picked camera** (`TimelineView.PickedCamera`) is the one I / ◆ Key key, and the one C / ✂ Cut to cuts to.
  - Pick it with 1–9, the picker (pads and VR reach it by focus), or a click on its name.
  - On a pad, a tap on X splits (on release), and holding X for 0.4 s cuts to the picked camera.
  - Live vision mixing: play, then press 2, C, 1, C …
- **Look through:** the Program (the camera of the cut under the playhead) or the picked camera.
- **Gizmos** (`CameraGizmos`, under `ClientWorld` like the stage):
  - Every keyed camera shows as an unshaded box with a cone lens down −Z, in its colour, with a `Label3D` name
    (no depth test).
  - Each is placed by `StudioCamera.PoseTransform`, the same math the view uses, so an aimed key's gizmo aims too.
  - All of them hide while looking through, so no camera ever films another.
- The timeline box grows with its rows up to a third of the screen, then scrolls.
- **Not yet** (#637): export of the program to a video, a multiview of every camera at once, depth of field.
