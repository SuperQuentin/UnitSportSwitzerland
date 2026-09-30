# Aerial combat

- **Aerial combat** (`src/Combat/`, `World/Combat` on server and clients — RPCs route by path):
  **fire (LMB / RB)** in the plane (two wing guns fixed along the nose, fired alternately), the
  helicopter (chin turret) or the **paraglider** (a gun in the pilot's hands). The turret and the
  hand gun converge on **what the crosshair is on** (`CrosshairPoint`: first hit or target along the
  camera ray, else 600 m) — converging on a fixed point 600 m out missed a wing 60 m away by 3 m,
  because the chase camera sits 7 m above the gun. A paraglider's **wing** has no collider, so rounds
  are also tested against a 10 × 2.2 × 2.6 m box 7.6 m over each paraglider pilot (`WingHit`) and a
  wing hit hurts the pilot. `IsShooter`: offline every body shares the shooter's authority, so
  "same authority = the shooter" made every other local body immune to our rounds. Items are on foot only and
  tricks/boost ground-mount only, so those bindings are free in the air. Rounds are **ray-marched
  tracers**, not bodies: 700 m/s + the craft's velocity, gravity, one ray per physics step from last
  to next position (so nothing tunnels), 2.5 s life, one `MultiMesh` of unshaded streaks. 14 rounds/s,
  7 damage, 500 rounds, rearmed on the ground. **Client-authoritative**: the shooter RPCs each
  round (unreliable, relayed by the server, which flies none) and every peer flies every round, but a
  peer damages only what it has authority over — its own player (`FootPlayer.ShotHit`: the vehicle
  takes it for its occupant, 0 HP wrecks it via the existing path), vehicles it parked
  (`VehicleBody.Health`/`Explode`), its own drones. The shooter id is the RPC sender, not a
  parameter. **Target drones** (`TargetDrone`, `AnimatableBody3D` with `SyncToPhysics = false` —
  synced, every transform write is reverted until the next physics frame and reads back stale):
  offline only, three red planes on banked circuits ahead of the player, spawned the first time the
  player opens fire, 60 HP, respawn after 8 s. HUD (`CombatHud`, layer 9): ring where the guns point,
  lead diamond on the target nearest the line of fire (relative velocity × round flight time), hit X,
  rounds left. Gun sound `SfxSynth.GunBank`. Check: `<godot> --path . -- --combatcheck[,out.png]
  [--at E,N]` — plane 400 m up, a drone keeping station 150 m down the boresight, holds the real
  `fire` action; non-zero exit unless it is hit and destroyed with the plane intact.
  `--craft paraglider`: a paraglider pilot fires at a second pilot's wing 66 m out; passes when
  rounds land and the target lost health. Works with no
  terrain (flies over a stand-in pad after 20 s). Untested with two real clients.
