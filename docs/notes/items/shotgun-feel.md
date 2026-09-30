# Shotgun feel (#100)

- **1P ADS**: `ViewPose.Aim` for Shoot = `(0, -0.105, -0.42)` pitched up 0.085 rad: the lighter rib
  between the barrels runs up from the receiver to the yellow front bead, which sits inside a small ring
  reticle (`BeadReticle` in `InventoryUi`, replaces the old "+" label) at the screen centre. Shotgun mesh:
  rib + bead added, barrels at y 0.03; retune the pose in screenshots (`gunshotcheck.sh`, `a_aimed`).
- **Fore-end is its own mesh** (`ItemDefs.ShotgunForeEnd`), a child of the in-hand mesh and of the
  viewmodel; `HeldItemVisual.Pump(delay)` slides it 0.11 m back and forward (`PumpTime` 0.30 s after
  `PumpDelay` 0.35 s). Works on every copy of the player (remote peers start it from the Shot event).
- **Recoil** (owner, `ItemController.Recoil`): `HeldItemVisual.Kick` + `Recoil` (up, back, roll,
  ~0.25 s), `FootPlayer.Punch(rad)` = additive camera pitch (`_punch`, 4.5 deg, decays exp 12/s, never
  enters `LookPitch`; applied in 1P, scope view and the 3P camera). The shake layer (`PlayerFeel`) has
  no public API, so the punch lives in `FootPlayer`.
- **Rate limit**: `ItemController` refuses the next shell until `PumpDelay + PumpTime + 0.1` s passed.
- **Shot event** (`ItemEvents.ShotEffect`, every peer): `FootPlayer.BodyJolt()` rocks the body back
  about the hips (remote and own 3P body), remote copies start `Pump()`, `SfxSynth.Pump` (two clacks)
  plays in 3D after `PumpDelay + 0.1`.
- **Muzzle origin**: `BirdLife.Fire` casts from `FootPlayer.EyePosition`. First person / scope view:
  along the camera. Third person hip-fire: a ray from the camera (started at the eye's depth, so the
  spring arm's wall is not hit) finds the target point, the cone aims from the eye at it.
- **3P shoulder pose**: `ItemArmPose.ShoulderAim` (see `item-arm-poses`); head tips onto the stock.
- Check: `GODOT=<exe> tools/gunshotcheck.sh [--gunside]` (`--gunshot A|B` probe, `ShotgunProbe`).
