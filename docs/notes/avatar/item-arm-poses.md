# Held items pose the arms (third person) and the held mesh follows the hand

- **Held items pose the arms, and the held mesh follows the hand** (#79). `ItemArmPose` (None, Hold,
  ShoulderAim, TwoHandEye, Mouth, Plant) is an arm-override layer in `HumanMeshBuilder.ApplyArms`: it
  blends the wrist targets (chest/neck/head relative, so they follow torso lean) from the gait's arms to
  the pose's and re-solves the elbows with `Limb.Solve`. Legs and gait are untouched. The item hand is
  the rig's -X wrist (the figure's right once the mesh faces -Z); pass `arm` + `armBlend` to
  `BuildStride` / `MountsFor` / `BuildPosed` / `MountsForPose`.
- Nothing arm-related is sent. Every peer derives the pose from `HeldItemId` (kind via `ItemDefs`) plus
  ONE replicated int, `FootPlayer.ItemAction` (0 idle, 1 aim, 2 use), which the owner's
  `ItemController` writes (today: aim = 1). Idle held item = `Hold`; aim: `Shoot` -> `ShoulderAim`,
  `Optic`/`Photo` -> `TwoHandEye`; use: `Consume` -> `Mouth`, `Place` -> `Plant`.
- Blend eases in/out over ~0.18 s per peer (`StepArmPose`: out of the old pose, then into the new). A
  remote copy snaps to its first received state instead of ramping, or `--synccheck`'s fresh-frame hand
  error is not 0.
- No view pitch is replicated, so aim poses follow body yaw only (barrel level); add a replicated pitch
  to tilt them.
- `GaitMounts.HandBasis` / `FootPlayer.HandLocal` (a full transform) carry the item direction: the pose's
  direction blended from the forearm; `HeldItemVisual` in-hand mode uses it instead of identity.
- The owner in 3P while aiming still gets the forced eye view (`ScopeView`); its own body is hidden.
- Check: `--synccheck` (fresh hand error must stay < 0.02); loopback `--server --generated-world` + two
  clients, one `--hold Shotgun --aim`, the other `--shot` at the holder (the remote copy shoulders it).
