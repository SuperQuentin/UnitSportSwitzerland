# Dropping, throwing and picking up world items (#206)

- **Dropped items** (`src/Items/DroppedItem.cs`, `DroppedItems.cs`, `World/Dropped` + `World/DroppedSpawner`):
  any `ItemStack` lying in the world as a `RigidBody3D`, the `RadioManager` pattern (offline `AddChild`;
  online `RequestDrop` / `RequestPickUp`, one winner, spawner removes it everywhere). The dropper is the
  authority over the fall (`Sync`: position, rotation, `Settled`), then frozen. Spawn data `DropState`
  carries the stack (id, count, `Data`), so a photo or a CD-carrying stack keeps its data. On a server
  with object containers (#689, `world/object-containers`) an item nobody is near sleeps in its tile and
  comes back, for 24 h; without them it is cleared after 15 min with nobody within 3 km.
  `MaxItems` 400 live (oldest goes), `ForgetOwner` respawns
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
  view ray first, else smallest angle in a ~18 deg cone within 2.6 m of the chest, if a ray from the eye reaches it;
  never one merely at the feet, #390)
  among dropped items and radios. The border is a **stencil silhouette** (#401) as `MaterialOverlay`:
  a pass that only writes stencil 77 over every mesh of the target (depth test off), then 8 next passes of
  the same meshes nudged 2 px (at 1080 lines) on screen in 8 directions, drawn only off the mark and marking
  what they draw (`stencil_mode read, write, compare_not_equal`): the outer edge only, constant width, no
  lines between sub-meshes, no normals (flat-shaded corners do not crack). Pulled 15 cm toward the eye so a
  flush car door's edge shows over the body (no more surface glow) while a hat in front still hides it.
  Stencil is read in the transparent pass only: both passes are transparent, ordered by render priority
  100 / 101. Another stencil user must not take 77. Replaced the inverted hull (lumpy, outlined every part). E on a pointed radio: a tap switches it on / off, a hold opens its panel (#725).
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
  Offline: `<godot> --path . -- --ride foot,60 --brake-at 0 --dropcheck solo` drops bars in front of
  the standing body, waits for them to float, points at them, screenshots `test_output/dropcheck_solo.png`,
  picks them up (without `--brake-at 0` the harness walks off before they settle). Every role
  uses a scratch inventory (`DropCheck.Requested`): `user://inventory.json` is shared by all worktrees.
- **Floating look** (`DropFloat.cs`): once `Settled`, only the visual changes — blown up so its largest
  side is 0.4 m (x1..x3.5; bigger items unchanged), upright, hovering 0.14 m over where the collider rests,
  spinning (~4 s a turn) and bobbing, Minecraft style. The body and collider stay on the ground. One loop in
  `DroppedItems._Process` for all items (no per-item process): posed each frame only within 45 m of the
  camera, frozen beyond, not drawn past 90 m (`VisibilityRangeEnd`). The pose is written in the body's
  local frame (it lies however it landed), so the origin shift needs nothing. The outline shader divides
  its push by the model scale so the rim keeps its width; `FlyToHand` starts from the blown-up scale.
- **Heft (#725).** `ThrowAim.HeftOf(item)` scales the arm's part of `Launch` (the player's own
  velocity is kept) and stretches the wind-up (`ChargeTime * (2 - heft)`): 1 for anything light,
  0.55 for the radio (tops out near 11.5 m/s, 1.45 s to full). `ItemController` sets `ThrowAim.Heft`
  every frame so the drawn arc matches; the pack panel's fixed throw uses it too. The radio body is
  7 kg with a low-bounce, high-friction `PhysicsMaterial` and half the old spin: it thuds and stays.
