# Creative mode and the fly camera (#1002)

- **T switches creative mode** on the body (`ClientWorld.ToggleCreative`, `FootPlayer.Creative`,
  `FootPlayer.Creative.cs`): inventory, tools and everything on foot stay; `TakeDamage` returns at
  once (falls, hits, blasts, drowning, the zone); jump twice within 0.3 s (`CreativeDoubleTap`) to
  take off or stop, as in Minecraft's creative mode.
- **Flying**: the stick moves on the ground plane from `_viewYaw` (pitch never climbs or dives),
  Jump held rises, Shift sinks (a pad's B, since L3 is `FlyBoost` with Ctrl), boost ×5, a little
  inertia (`Damp(8)`). Sinking onto the floor lands and ends the flight. While flying the view
  looks straight up and down (`ClampPitch`; the third-person clamp keeps the lens off the ground and
  comes back on landing), and `PlayerFeel` draws no speed lines and no shake. **The flight step must
  call `UpdateCameraFeel`** before it returns, as the swim does: the first-person eye takes its pitch
  there, at the end of the walk step, and without it the view stayed level (#1002 playtest). It runs after the swim, fight,
  beam and aura steps, before the walk, so water and duels still win.
- **The figure flies like Superman** (#1219): `PoseFly = 8` (`PublishFlyPose`), `Anim.X` the lean 0..1
  from the forward speed in the body's frame (full at `FlyFullLean` 9 m/s, eased at `Damp(4)`), and
  `BodyPose` tips the figure about its middle up to 80° (`FlyTilt`), banks it a little strafing, back a
  little flying backwards, head down a little diving, with a slow bob hovering. `HumanMeshBuilder.FlyRig`
  turns the arms between the hover (loose at the sides, legs dangling) and Superman (right fist up
  over the head, left arm along the body, legs straight, the left knee bent, toes pointed, head tipped
  back to look ahead); the hover's paddle runs on each peer's clock (`_flailClock`). Third person, the
  flight turns the body to where it flies (`FaceTravel`). Protocol 50: old peers would draw kind 8 as
  a stride. The check asserts upright hovering, lying out flying, standing up again; windowed it saves
  `test_output/creative/{hover,superman}[_side].png`.
- **`GameSettings.CreativeNoclip`** (Settings > Controls > "Creative flight through walls", off):
  the flight moves the position without `MoveAndSlide`; `RescueFromVoid` waits while it flies, so
  flying into a hill does not snap you onto it; landing underground does.
- **Refused like the fly camera**: in a Battle Royale match, and online on a server without
  teleporting (`Permissions.CanTeleport`); `OnPermissionsChanged` turns it off when either starts.
- **Local only**: not replicated, no protocol change. Damage is decided on the owning peer
  (`FootPlayer.TakeDamage`), so the owner's flag is enough; remote peers see the replicated fly pose.
  A creative player can still hurt others (as on a Minecraft server).
- **Spectator** (the camera-only `SpectatorCamera`, with the mouse wheel's speed): `PlayerInput.Spectator`,
  Shift+T (told from T by `exactMatch`, checked before `Tune`); a pad holds D-pad down 0.4 s
  (`ClientWorld._modePadAt`, a tap is T, as the quick menu tells Back's tap from its hold); VR the
  wrist menu's "Spectator camera". It goes back the same way. The world still starts on the fly
  camera and drops into the body; on the fly camera T also goes back into the body, which the
  probes that press T rely on. `InputHints` names chords ("Shift+T").
- **`SpectatorCamera` level flight** (`GameSettings.LevelFlight`, default on): WASD on the ground
  plane from `Rotation.Y` (not `_yaw`: `FlightProbe` sets the rotation directly), Space / Shift height;
  off, forward follows the look.
- Bots: not for bots (a client toggle); a bot's body never reads it.
- Check: `--creativecheck --world flat` (`Player/CreativeCheck`).
