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
  landing-dip spring spent as a squash. Mounted first person sits at the figure's own eye
  (`Rideable.FirstPersonEye` from `MountsForPose`), rolled with the lean. In a car it is the
  cockpit, a cycle of three (chase, with your body, without): `cockpit`.
  Screenshot the player's view with `--ride foot|bike|skis,seconds,out.png` (`foot` stands still).
