# The VR rig: following the game's camera (#186)

- **`XR/XrRig`** is top-level, with `ProcessPriority` 1000 so it runs after every game node. It
  holds an `XROrigin3D`, an `XRCamera3D` and two `XRController3D` (aim pose).
- **The anchor.** The game keeps placing its cameras exactly as it does flat. Whenever one is made
  current (`player.Camera.Current = true`, the spectator, a GPX shot), the rig takes it as the
  **anchor** and makes its own camera current again. `XrSession.Anchor` is "the camera the game
  wants to look through". Use it wherever code asked `GetViewport().GetCamera3D()` to find the
  viewer (`ClientWorld.Prompts`, `FootPlayer.IsViewing`).
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
  - Teleport is not done yet.
- Hands are small boxes until the avatar's arms are driven (phase 2, see `roadmap`).
