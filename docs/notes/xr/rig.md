# The VR rig: following the game's camera (#186)

- **`XR/XrRig`** is top-level, with `ProcessPriority` 1000 so it runs after every game node. It
  holds a `SubViewport` named `Headset` with `UseXR` on. Inside it are an `XROrigin3D`, an
  `XRCamera3D` and two `XRController3D` (aim pose). The SubViewport shares the game's
  `World3D`.
  - The headset renders through that SubViewport, not the root viewport. This is the documented
    Godot way to keep the desktop window free.
  - The window keeps rendering the game's current camera, so it becomes the monitor view
    (`monitor`).
  - With `--xrsim`, the SubViewport is not updated.
- **The anchor.** The game keeps placing its cameras exactly as it does flat. The window's current
  camera (`player.Camera`, the spectator, a GPX shot) is the **anchor**. `XrSession.Anchor`
  exposes it. When the rig adopts a camera as the anchor, it strips `HeadsetOnlyLayer` and
  `SpectatorOnlyLayer` from that camera's cull mask.
- **Anchors that are not a player's camera** (the spectator, a GPX shot, the title backdrop) are
  followed in position only. The heading is the one the camera had when it was adopted: a camera
  that orbits or pans on its own would spin the world round you.
  - The title's backdrop camera (under `Ui.TitleDiorama`) drifts along its orbit, so it is held in
    position too, from its first placed frame.
- **Tonemap.** If the scene's environment is not linear (the title backdrop is Filmic), the
  headset camera gets a copy of that environment with a linear tonemap (`UpdateTonemap`). The
  reason: the UI panel is a 3D surface, and the Mobile renderer clamps colour before tonemapping,
  so under Filmic its white topped out at about 63 % grey. A colour gain does not help.
- **Controller markers** are hidden while their controller is untracked. An untracked controller
  sits at the origin, inside the head.
- **Calibration.** The tracking space is placed so that the head, at the yaw-only pose it had at
  the last recentre, sits exactly on the anchor: `origin = anchor * calib`, with
  `calib = inverse(head0)`.
  - It recentres automatically 0.5 s after tracking starts.
  - Holding the right stick in for 0.8 s recentres too.
  - It works seated and standing alike: head height is whatever it was at the recentre.
- **A player's anchor is VR-aware.** While `XrSession.Active`, `FootPlayer` changes three eyes:
  - On foot, the eye has no bob, landing dip, roll or pitch.
  - The mounted first-person eye has no roll and no free look.
  - The cockpit eye has no head basis, no sway and no shoulder lean.

  So the anchor carries the body's frame: level on foot, pitched and rolled with a vehicle, which
  keeps the cockpit in place around you. Non-player anchors (the spectator) are followed in yaw
  only. Third person is off in VR; V in a cockpit only toggles your own body.
- **The head is written back.** After placing the origin, the rig copies the head's global
  transform onto the player's own camera. Everything that aims along `FootPlayer.Camera` (guns,
  the camera item, birds, combat raycasts) then aims where you look.
  - If the game did not re-place its camera in a frame, the anchor still equals the head we wrote
    back. The rig detects this and reuses the previous anchor; otherwise the origin would drift by
    the head offset every frame.
- **Comfort.**
  - Snap turn of 30° on foot (right stick X). In vehicles the head is the free look.
  - A vignette (`shaders/xr_vignette.gdshader`, a clip-space quad per eye) driven by the anchor's
    speed and yaw rate. It is halved in a first-person mount or cockpit.
  - `XrRig.Blink()`: black at once, clear again in 0.4 s (the vignette's `blackout`), to hide a cut. Used by the
    crash view (`player/crash-ragdoll`).
  - Teleport is not done yet.
- Hands are small boxes until the avatar's arms are driven (phase 2, see `roadmap`).
