# Dropping, throwing and picking up world items (#206)

- **Dropped items** (`src/Items/DroppedItem.cs`, `DroppedItems.cs`, `World/Dropped` + `World/DroppedSpawner`):
  any `ItemStack` lying in the world as a `RigidBody3D`, the `RadioManager` pattern (offline `AddChild`;
  online `RequestDrop` / `RequestPickUp`, one winner, spawner removes it everywhere). The dropper is the
  authority over the fall (`Sync`: position, rotation, `Settled`), then frozen. Spawn data `DropState`
  carries the stack (id, count, `Data`), so a photo or a CD-carrying stack keeps its data. Not saved;
  `MaxItems` 400 (oldest goes), cleared after 15 min with nobody within 3 km, `ForgetOwner` respawns
  a leaver's items server-owned. Radios still go through `RadioManager` (they play); everything else here.
- **No waiting on the network.** The dropper adds a local `Proxy` at once (token in `DropState.Token`);
  the server's spawn takes over from wherever it got to (`MultiplayerSpawner.Spawned` -> `TakeOver`).
  Other peers simulate the spawned body from its spawn velocity (`_predicting`) until the dropper's first
  `Synchronized`, then freeze it kinematic. Without this a drop appeared 8-10 s late during the boot burst
  of terrain streaming (reliable channel congested; spawns and on-change states arrive in one burst).
- **Fast throws tunnelled** through the terrain (a 21 m/s stone fell 350 m): `ContinuousCd = true`, plus
  a rescue in `_PhysicsProcess` (outdoors, more than 1 m under `DroppedItems.GroundHeight`: put on top).
  Interiors are at y = -3000, so the rescue only acts above y = -1000.
- **Keys:** Q drops one of the held item, Ctrl+Q the stack (`PlayerInput.DropItem`, keyboard only); the
  pack panel has "Drop on the ground" (whole stack). E picks up what is pointed at (room checked first;
  the item flies into the hand as a local ghost, `ItemController.FlyToHand`).
- **Pointing + outline** (`Highlight.cs`): every frame `ItemController` picks the target (`Highlight.Find`:
  view ray first, else smallest angle in a ~18 deg cone within 2.6 m of the chest, else one at the feet)
  among dropped items and radios. The border is an inverted hull as `MaterialOverlay`, pushed from the
  mesh's AABB centre (not normals: flat-shaded items would crack), width scaled by view distance, pulsing;
  `instance uniform center` so one material serves all. E on a pointed radio opens its panel.
- **Throw** (`ThrowAim.cs`): items with `ItemDefs.Throwable` (Throw, Consume, Material, Wear use) — Aim
  shows the arc, Use winds up (0..1 over 1 s, ease-out; hum `SfxSynth.ChargeHum` rising in pitch, chime +
  shake at full), letting go of Use throws (`ReleasePower`), letting go of Aim cancels. Velocity: view
  direction lofted 0.2 rad, 4.5-21 m/s, plus the player's velocity; origin over the right shoulder. The arc
  is the same simulation (project gravity and linear damp, 1/60 s steps, a ray every two steps, excluding
  the player), drawn as scrolling camera-facing dashes plus a ring on the hit surface. FOV 62 -> 52 with the
  wind-up, a pop wider on release. A tap of Use while aiming still throws (power about 0).
- **Shoulder camera** (`FootPlayer.ThrowAim`, `CameraShake`, re-asserted per frame): third person eases in
  to 1.7 m over the right shoulder, body squares up to the view (`FaceTravel`). In first person, third person
  is *lent* (`_borrowedThird`, `RefreshVisual` builds the body) and the arm grows out of the eye; given back
  once eased out. V mid-throw gives the lent view back first.
- **Arm poses seen by others:** `ItemAction` 3 = `ItemArmPose.ThrowWindup` (item hand cocked above the
  shoulder, other arm pointing), 4 = `ThrowRelease` for 0.4 s after the throw; release snaps straight out
  of the wind-up (`StepArmPose`). Checked on the remote copy by `--dropcheck watch`.
- **Landing juice** (`ImpactFx`): watches the parent's position on every peer (simulated or synced alike),
  a hard stop after falling > 2.2 m/s = 3D thud (`SfxSynth.LandingBank`, pitch by size) + dust burst. On
  radios too. `Rebase()` after a proxy hand-over so the jump is not read as a landing.
- **Check:** `tools/dropcheck.sh` (`CHUNKS=<dir>` from a worktree). Windowed thrower + watcher on loopback;
  read the RESULT lines and `test_output/dropcheck_aim.png` / `dropcheck_point.png`. The two trade a held-item
  ping first (bars in the thrower's hand -> a stone in the watcher's): right after a join everything
  reliable to the second client can arrive ~10 s late and in one burst, which swallows a wind-up and its
  release. Under load (other sessions' loopback checks) a windowed client can also time out its link; rerun.
  Offline: `<godot> --path . -- --ride foot,60 --dropcheck solo` drops bars, points at them while the
  `--ride` harness walks past, screenshots `test_output/dropcheck_solo.png`, picks them up. Every role
  uses a scratch inventory (`DropCheck.Requested`): `user://inventory.json` is shared by all worktrees.
