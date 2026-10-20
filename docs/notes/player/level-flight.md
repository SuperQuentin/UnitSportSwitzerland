# Free fly: level flight (Minecraft spectator style)

- `SpectatorCamera` (free fly) moves like Minecraft's spectator / creative flight by default (#1002):
  WASD / left stick move on the ground plane from the camera's yaw only, so pitch never makes you
  climb or dive; Space / E / RT up and Shift / Q / LT down are the only height controls.
- `GameSettings.LevelFlight` (default on; Settings > Controls > "Level flight") turns it off: forward
  then follows the look, the old free-cam flight.
- The yaw comes from `Rotation.Y`, not the private `_yaw`: `FlightProbe` sets the camera's rotation
  directly, and the walk direction must match what is on screen.
- No new key, no network state: the fly camera is local and bots never use it.
