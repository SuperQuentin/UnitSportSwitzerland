# Viewmodel poses

- `HeldItemVisual` holds the first-person viewmodel at a named `ViewPose` (Rest, Aim, Eye, Mouth,
  Plant, Inspect); `PoseTransform` gives position + euler per pose, per item use (Shoot Aim = barrel line
  on screen centre, Photo Eye = camera low in front of the eye, Optic Eye = binoculars at the eyes).
- API: `SetPose(ViewPose)` eases (exp damping) toward a pose; `PoseBlend01` 0..1, `PoseSettled`
  (arrived, no one-shot); `PlayOneShot(ViewPose target, float inTime, float hold, float outTime,
  Action? onPeak)` does a there-and-back over the current pose (eat, plant...), `onPeak` fires once on arrival.
- `ItemController` sets Aim for Shoot, Eye for Photo/Optic, else Rest. It hides the item only during
  photo capture and for binoculars once settled; `Ui.Scope` for Photo/Optic waits for `PoseSettled`
  so the overlay appears after the raise. Shoot crosshair shows at once, gun stays visible.
- Sway is cut to 25% outside Rest; Kick recoil works in every pose.
- Wall clipping: the viewmodel is drawn at `ViewScale` = 0.5 size and pulled in by the same factor
  (identical on screen), so a shotgun pokes half as far forward. Chosen over depth-test-off
  materials (break self-occlusion, sorting) and a lower camera Near (FootPlayer, shared).
- Screenshot: `--ride foot,6,out.png --hold shotgun --view first --aim`.
