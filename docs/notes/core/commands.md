# Commands

- Spawn elsewhere: `<godot> --path . -- --at <lv95E>,<lv95N>` (default: Riddes,
  2583250/1113250), or `--goto <town>` to name it instead of looking up coordinates.
  `SpawnPoint` drops the camera to ground + 220 m once the chunk beneath it streams in — the
  height cannot be known at boot.
- Screenshot without the editor: `<godot> --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png`
  (also prints fps/prims/draws — the way to verify rendering when the godot-ai MCP is down).
  `ClientWorld` skips `SpawnPoint` when `--shot`/`--probe` is given, otherwise the spawn
  drop overwrites the requested y with ground + 220 m and every close-up shot comes back
  as an aerial one. `ShotRunner` also re-claims `Current` every frame — a mode entered from
  a deferred call (GPX replay) would otherwise steal the camera after the shot was set up.
  Add `--menu` to capture the mode picker.
