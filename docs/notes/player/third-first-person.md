# Third / first person

- **Third / first person** (`FootPlayer`, **V / R3**, saved as `GameSettings.ThirdPerson`, default
  third; `--view first|third` for one run). On foot the mouse/stick turn a **view yaw**
  (`_viewYaw`), not the body: first person sets the body to it every render frame (the old
  behaviour exactly), third person lets the body turn to face its travel (`FaceTravel` — toward
  the input while there is some, else the velocity) and orbits a spring-arm camera
  (`UpdateThirdPersonCamera`, a 0.2 m sphere sweep `ArmHit`, not a ray: a ray grazing door frames
  indoors flickered hit/miss and shook the lens) from above the right shoulder in **global** space — parented to the
  turning body it would swing round every direction change. Movement is relative to the view, so
  forward is into the screen in both. The shoulder is `GameSettings.LeftShoulder` (`swap_shoulder`,
  H / middle mouse, `--shoulder left|right`; #460); aiming a gun in third person (not first) pulls in to a close shoulder camera like a
  throw (`shotgun-feel`). Both cameras update in `_Process`, not physics, or look lags
  the mouse by up to a physics tick. The local body is the same `HumanMeshBuilder` figure remote
  players see: solved gait grounded, `Running` pose airborne (>0.12 s), `Tucked` sliding, and the
  landing-dip spring spent as a squash. First person keeps that figure too, hidden from the eye
  only, for your shadow and the mirrors (`avatar/own-body-from-eye`, #833). Mounted first person sits at the figure's own eye
  (`Rideable.FirstPersonEye` from `MountsForPose`), rolled with the lean. In a car it is the
  cockpit, a cycle of three (chase, with your body, without): `cockpit`.
  **Zoom** (#1149, #1369, Genshin's way): the plain wheel, whatever is held (Shift is sprint), / `=` `-`
  (`view_zoom_in/out`; the hotbar is Alt + wheel, `PlayerInput.IsHotbarWheel`; no zoom on a wheel the held
  item owns, `ItemController.WheelClaimed`, nor with a released pointer), a pad holds R3 and pushes the right stick (a tap of R3
  is still the view switch, on release). `_zoomWant` 0.6..8 m (default `ArmLength`) in ×1.2 steps,
  `_zoom` eased; closer than `ArmLength` the pivot climbs to `EyeHeight`, the shoulder offset and the
  speed pull-back go (`face`), so the nearest stop frames the head: orbit round to the front for the
  face. Owner only, not saved, not in VR; throws, guns and the phone lerp over it as before.
  Screenshot the player's view with `--ride foot|bike|skis,seconds,out.png` (`foot` stands still).
