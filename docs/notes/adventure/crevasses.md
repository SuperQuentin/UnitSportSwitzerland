# Hidden crevasses: the slot, the rope catch, the climb out (#1372)

Plan: `docs/plans/adventure-together.md` ("Hidden crevasses"). Code: `src/Adventure/Together/` (`Crevasses`, `CrevasseNode`, `TogetherService.Crevasses`), `FootPlayer.Together` (`EnterCrevasse`, `PoseCrevasse`), `Net/StaminaWatch`. The first build (#1266) shipped only a stumble; this is the real slot.

## What happens

- `Crevasses.At(e, n)` (pure, tier 0): 12 m cells of glacier cover, 4 % hold a hidden strip 1.5 m wide at an angle of the cell's own hash (the plan said "down the slope"; the pure function has no slope, so the heading is the hash's). Each cell breaks once per session (`_broken`).
- **Owner only.** On foot on glacier cover (`ChunkManager.TryGetCover`, or a check's `--together-glacier`), a step onto a strip breaks the bridge: a `CrevasseNode` opens on the strip's line under the body (an ice pit 1.5 m wide, 14 m long, 10 m deep, built once with the node, freed after 120 s). Its collision (two walls, two ends, a floor) is on `FootPlayer.CrevasseLayer`.
- **The ground is let go for that body.** `EnterCrevasse()` takes layer 1 (terrain, roads, buildings) out of its `CollisionMask` and the slot's layer in; `LeaveCrevasse()` puts it back. Nobody else's mask has the slot layer, so the slot is invisible to everyone else's physics. `SoftLanding` is set for the fall (no damage). `RescueFromVoid` stands down while `InCrevasse`.
- **Unroped**: it falls 10 m onto the ice floor and climbs a wall out: the walls are plain static bodies, so the climbing rules (`Climbable`) take them; the stamina meter pays (a scripted wish into the wall climbs, as in `ClimbCheck`). Out = the feet are back at the ground (`Crevasses.Out`: it went 2 m down, then `y >= ground`); the terrain layer returns that tick. After `TogetherService.RescueAfter` (60 s) in the slot, or a body that falls through the floor, it is set on the lip. A respawn or teleport (more than 14 m from the slot) just lets go.
- **Roped** (`Rope` mode, the partner standing within `LinkBook.RopeLength`, `Crevasses.CaughtByRope`): the fall is held by the rope, which runs over the lip: the body hangs 3 m under it (`Crevasses.CatchDepth`, `Owned.Crevasse` and `Lip`, `RopeStep` anchors there). The partner hauls with the held E (`HaulTarget` counts `PoseCrevasse` as hanging), or the hanging player climbs. A haul shortens the rope to the lip (`HaulSpeed` 0.6 m/s); the lip anchor is 0.3 m over the snow so the feet end on it.
- Sounds: `TogetherSounds.Crack` at the break, `Thud` on the floor, the existing `Zip` on a held fall. Toasts say what to do.

## What the others see, what the server sees

- **`PoseKind` 9 (`FootPlayer.PoseCrevasse`)** is replicated while the body is in the slot (the owner overrides what the walk or climb published; `Anim.W` = 0 stands or hangs, 1 climbs, 2 falls). `ApplyFootPose` draws the figure `Anim.W` names (`ShownPose`): the flail, the climb, the stride. Nothing else is sent: no RPC of its own.
- The server sees a body 10 m under the ground. `StaminaWatch` skips a `PoseCrevasse` body (for at most `RescueSeconds + 5` s), keeps its climb baseline at the body's height and logs once `[stamina] <name> is down a crevasse: not watched`. There is no other speed check on the server today (Versteckis has its own, never on a glacier).
- Protocol: a new `PoseKind` value and `Arrival.Rules` carrying the server's `--crevasses` switch.

## Switches

- `Settings › Gameplay › Crevasses` (on by default); the server's `--crevasses off|on` (`Arrival.Crevasses`, `Permissions.ServerCrevasses`) decides online; a `--crevasses off|on` on a client's own command line wins over its setting (offline, checks). Never in a Battle Royale match. `Permissions.CrevassesOn` is the one test.
- Test seams: `--together-glacier E,N,R` (pretend glacier), `TogetherService.RescueAfter`.

## Three devices, bots

- Falling is not an action. Climbing out is the existing free climb (`xr/vr-action-map`: stick into the wall on keyboard and pad, the hand climb or the pad route in VR); the haul is the existing held E / hold Y (`links`). No new key.
- A bot never falls: the server builds no crevasse (`bots/bot-capability-map`).

## Checks

- Tier 0 `CrevassesTests` (density, strip width, the slot's line, the rope rule, `Out`, `Stuck`).
- Quick `--crevassecheck --world fixture --systems ui,terrain,physics,adventure`: setting off, the unroped fall (own collision, mask, 10 m, floor, no damage, pose 9), the climb out and solid ground, the rescue, the roped catch at 3 m and the haul.
- Net `tools/crevassenetcheck.sh` (Lea and Tom): Tom sees Lea's copy in pose 9 ten metres under him, then on the ground; the roped catch seen from Tom, his haul, the rescue; the server log shows the watch skipped it and flagged no climb.
- Model viewer: `Crevasse` (category Adventure, `CrevasseNode.Slot`). Sound player: `Crack`, `Thud`.
