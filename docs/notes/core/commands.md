# Commands

- Window title: `--title "..."` names the window; without it the title shows the run's user
  args (minus the `--chunks`/`--cache` paths), so parallel test windows can be told apart.
  Agents launching windowed runs pass `--title "<issue> <what is tested>"`.
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
  Add `--menu` to capture the pause menu over the world (the title screen: `--uishot`, `ui/screens`),
  `--settings` or `--licenses` (Settings on its About tab) for those pages. `--nohud` hides every
  `CanvasLayer` (chat, key hints, menus) in the picture.
- Many screenshots, one launch: `<godot> --path . -- --shot-queue shots.txt [--nohud]` boots like
  `--shot`, then watches the file: one shot per line in the `--shot` syntax, taken in order,
  lines appended later picked up within 0.5 s; blank lines and `#` comments skipped, `quit`
  exits (code 1 if any shot or line failed). A file shorter than what was read is a new queue.
  Each PNG is written as `<out>.part` and renamed, so poll for the final name. Use it whenever
  more than one picture is needed: every launch steals focus on macOS (`macos-launch-steals-focus`;
  Windows: `windows-launch-focus`).
  A queued shot's y may be `g1.7` (that high above the ground, once it has streamed in), and each
  shot logs `frame=` ms, averaged over its last second of settling. Or `i1.6`, inside a house
  (#320, offline): x, z stand in front of a front door (within 30 m, outside: a point inside a
  building never finds its own door); that door opens, and once its interior is built the camera
  goes as far behind the doorway as the point stands in front of it, 1.6 m above the sill, turned
  as asked, carried into the rooms (`InteriorManager.OpenDoorForCamera`/`CameraInside`). Stand
  2-3 m from a house looking at it to look into its ground floor. The door is left open, so
  queue it last; it fails after 40 s without a door or an interior.
  A line `shift dE,dN` moves the floating origin by that many metres (LV95 E, N) with the camera
  still, then logs the CPU and GPU time of the 120 frames after it against the 30 before (#185):
  what a shift costs the renderer. Time it with no shot right after it (saving a PNG stalls the
  GPU); picture it in a second run, with settles of 0 for the very next frames.
- `--origin E,N` (LV95): pins the starting world origin, so shots at fixed world coordinates stay put
  when the manifest's suggested origin moves. The floating origin still moves it as the camera
  travels (`ShotRunner` maps queued shots from that first frame); add `--originshift 1000000` to
  keep it still. Online too: every peer has its own origin since #185. On a server it moves the
  server's own world space (the server never shifts): `--server --origin 3583250,1113250` runs a
  generated world 1,000 km from the server's origin, the check that nothing on the server depends on it.
- `--nocapture`: never grab the mouse (`Core/MouseCapture`). Every probe and tool run implies it,
  so a check running in a window leaves the pointer to whoever is using the machine.
- `--chatcheck`: chat tab completion, `/spawn` parsing and Up/Down history (a real `ChatUi`), headless, RESULT PASS/FAIL (`Core/ChatCheck`).
- Floating origin (`floating-origin`): `--origincheck` (headless, RESULT PASS/FAIL), `--originstress <m>`
  (shift past `m` metres, to the metre: add it to any probe), `--originshift <m>` (another threshold,
  still snapped to whole km).
- Steering wheel (`steering-wheel`): `--wheelcheck`, headless, RESULT PASS/FAIL — direct steering for
  every car in both profiles, range stretch, pedal read-out, pad bindings off an ignored joypad and back,
  SDL3 loads. Loopback: server `--server --port P --generated-world`, then clients
  `--connect 127.0.0.1:P --wheelwatch B` and `--connect 127.0.0.1:P --wheelwatch A --fakewheel` (A drives
  a car on a swept simulated wheel, B must see it steer both ways). `--fakewheel` alone plays with that
  wheel; `--wheel on|off`, `--wheelrange deg` override for one run; `--settings wheel` opens Settings on
  the Wheel tab. `--ffbcheck` (window, real wheel, hands off): pushes it right then left at 30% for
  0.5 s and reads back which way it turned; RESULT PASS/FAIL, then pushes into a simulated 60° soft lock.
  `--wheellock deg` gives every vehicle that lock to lock (steering and cockpit wheel follow);
  `--ffblog` prints what the wheel is given every 2 s.
