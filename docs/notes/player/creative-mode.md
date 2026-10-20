# Creative mode and the fly camera (#1002)

- **T switches creative mode** on the body (`ClientWorld.ToggleCreative`, `FootPlayer.Creative`,
  `FootPlayer.Creative.cs`): inventory, tools and everything on foot stay; `TakeDamage` returns at
  once (falls, hits, blasts, drowning, the zone); jump twice within 0.3 s (`CreativeDoubleTap`) to
  take off or stop, as in Minecraft's creative mode.
- **Flying**: the stick moves on the ground plane from `_viewYaw` (pitch never climbs or dives),
  Jump held rises, Shift sinks (a pad's B, since L3 is `FlyBoost` with Ctrl), boost ×5, a little
  inertia (`Damp(8)`). Sinking onto the floor lands and ends the flight. It runs after the swim, fight,
  beam and aura steps, before the walk, so water and duels still win.
- **`GameSettings.CreativeNoclip`** (Settings > Controls > "Creative flight through walls", off):
  the flight moves the position without `MoveAndSlide`; `RescueFromVoid` waits while it flies, so
  flying into a hill does not snap you onto it; landing underground does.
- **Refused like the fly camera**: in a Battle Royale match, and online on a server without
  teleporting (`Permissions.CanTeleport`); `OnPermissionsChanged` turns it off when either starts.
- **Local only**: not replicated, no protocol change. Damage is decided on the owning peer
  (`FootPlayer.TakeDamage`), so the owner's flag is enough; remote peers see the replicated air pose.
  A creative player can still hurt others (as on a Minecraft server).
- **The old camera-only fly mode** (`SpectatorCamera`) is behind `GameSettings.FlyCameraOnT`
  (Settings > Controls > "T opens the fly camera", off). The world still starts on the fly camera
  and drops into the body; on the fly camera T always goes back into the body, which the probes that
  press T rely on.
- **`SpectatorCamera` level flight** (`GameSettings.LevelFlight`, default on): WASD on the ground
  plane from `Rotation.Y` (not `_yaw`: `FlightProbe` sets the rotation directly), Space / Shift height;
  off, forward follows the look.
- Bots: not for bots (a client toggle); a bot's body never reads it.
- Check: `--creativecheck --world flat` (`Player/CreativeCheck`).
