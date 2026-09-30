# Viewmodel poses

- `HeldItemVisual` holds the first-person viewmodel at a named `ViewPose` (Rest, Aim, Eye, Mouth,
  Plant, Inspect, Raise); `PoseTransform` gives position + euler per pose, per item use (Shoot Aim = barrel line
  on screen centre, Photo Eye = camera low in front of the eye, Optic Eye = binoculars at the eyes).
- API: `SetPose(ViewPose)` eases (exp damping) toward a pose; `PoseBlend01` 0..1, `PoseSettled`
  (arrived, no one-shot); `PlayOneShot(ViewPose target, float inTime, float hold, float outTime,
  Action? onPeak)` does a there-and-back over the current pose (eat, plant...), `onPeak` fires once on arrival.
- `Raise` (flag lifted before the stab) is set by the flag stroke (`flag-plant`).
- `ItemController` sets Aim for Shoot, Eye for Photo/Optic, else Rest. It hides the item during
  photo capture and for binoculars and the camera once at the eye (you look through them, never at them); `Ui.Scope` for Photo/Optic waits for `PoseSettled`
  so the overlay appears after the raise. Shoot crosshair shows at once, gun stays visible.
- Sway is cut to 25% outside Rest; Kick recoil works in every pose.
- Wall clipping: the viewmodel is drawn at `ViewScale` = 0.5 size and pulled in by the same factor
  (identical on screen), so a shotgun pokes half as far forward. Chosen over depth-test-off
  materials (break self-occlusion, sorting) and a lower camera Near (FootPlayer, shared).
- Screenshot: `--ride foot,6,out.png --hold shotgun --view first --aim`.
- **Use animations** (#108): `ViewPose` also has `Read` (GPS held up, low centre, top tipped away) and `Head`
  (hat lifted above the eye line). `PoseTransform` Mouth depends on the item: a bottle tips ~70 deg, food jabs
  up and in. `PlayOneShot(..., onPeak, onEnd)`; `CancelOneShot()`; `OneShotActive`. The one-shot clock (`StepShot`)
  runs in every view (third person has no viewmodel). `ItemController.StartUse` wraps it: eat/drink =
  Mouth 0.35/0.6/0.3 s, hat = Head 0.3/0.2/0.3 s; the effect (heal + item taken, or `Inventory.SetWorn`) fires at the
  peak; switching item before the peak cancels (nothing happens); a second use while busy is ignored; `ItemAction`
  = 2 for the duration so remote peers pose Mouth (Wear maps to Mouth too). Sounds: `SfxSynth.GulpBank` (water),
  `CrunchBank` (food).
- **Hats in hand** are the real worn hat: `ItemDefs.HandMesh` calls `HumanMeshBuilder.AppendHat` (internal).
- **GPS screen**: first person draws the readout onto the device: a `SubViewport` (96x84, Label on an LCD-green
  ColorRect) shown as a `QuadMesh` on the device's face (mesh faces +Z, the holder). Chosen over the HUD panel
  because the numbers live on what you hold; the HUD panel stays for third person. Four short lines
  (`GpsScreen`). Only rendered while a GPS is the viewmodel.
- **Binocular breathing**: while aimed, FOV drifts +-1.2% (slow sine) and the overlay shader drifts (`OpticSway`).
- Check: `GODOT=<exe> tools/useanimcheck.sh` (`--useanim A|B`), screenshots `test_output/useanim_*.png`.
