# Day/night

- **Day/night** (`World/DayNight`): four **global shader uniforms** declared in `project.godot`
  `[shader_globals]` — `world_sun_dir`, `world_tint`, `world_sky`, `world_night` — written once a
  frame reach every `ps1_*` world shader (each multiplies by the tint just before its Bayer
  quantise and fogs toward the sky) with no material touched. The environment background and
  ambient follow the sky, because avatars and vehicles are standard materials lit only by that.
  The real sun and moon (#1430, next bullet); below the horizon the shading follows the moon, held
  at least ~25° up, so mountains keep a lit side. **Tint and sky are authored as seen and converted
  `SrgbToLinear` before upload** — the shaders multiply linear values, and unconverted night's 0.13
  displayed as 0.40. Buildings light far more windows at night and those glow through the dark.
  Settings → Time of day (start hour, day length, default 24 min, 0 = stopped); `--time <h>` fixes
  the hour for one run. **`/time`** (#202, `World/TimeCommand` parses, `Net/ChatManager` applies):
  `set <hh:mm|sunrise|day|noon|sunset|night|midnight> [zone]`, `add <hours>`, `speed <min a day, 0 stops | normal | real>` (`normal` = 24 min, `real` = 1440 min, a day a day; `TimeCommand.MaxMinutesPerDay` is a real day, the settings' slider stops at 240);
  bare `/time` answers from this screen's clock. Offline it moves this machine's clock
  (`DayNight.DayLengthOverride` for speed).
- **The real sky** (#1430 phase H6, `World/SunPath`, `Core/Ephemeris`, `Core/SwissTime`): the
  sun and moon by Meeus (sun 0.008°, moon 0.0013° against JPL Horizons) on `Calendar.Today` at the
  game's hour, read as Swiss civil time (CET/CEST, the EU rule, no `TimeZoneInfo`), from the
  camera's place (`DayNight.Place`; Bern before a camera is out, the last place indoors): in an anchor frame the
  camera itself, its height included (#1845, `world/night-sky`), on the flat ground its LV95 kilometre, turned from
  true north to grid north (the meridian convergence, ±1-2° at the borders). Bern's day
  runs 08:13-16:44 at the December solstice and 05:36-21:29 in June, 66.5° high at noon.
  `/time set sunrise|sunset` are today's at Bern (`SunPath.RiseOrSet`) at home; away from Switzerland every
  named hour is the sun's where the player stands and hours read in their zone (`world/local-time`, #1801). The thermals read the same
  sun at the thermal's place (`AirLift`), so they follow the season. One date per process: the
  server's and the clients' are each their own `Calendar.Today` (`--date` pins it).
  **The fixed day** (`SunModel.Fixed`): up 6h, due south and 62° at noon, down 18h, the moon
  opposite the sun, an occasion's own sunrise/sunset/noon. `--sun fixed`, and the default of checks
  (any `--*check`), narrowed and fixture worlds (`--world`, `--systems`, `--chunks fixture:`),
  screenshot runs (`--shot`, `--shot-queue`), trailer renders and the fun world, so their results
  and pictures do not move with the date; `--sun real` (with `--date`) forces the real one there. Unit tests: `EphemerisTests`, `RealSunPathTests`.
  The moon is drawn in the sky too (`World/MoonDisc`, #1440, `items/telescope`): `DayNight.Sky` holds
  the real sun and moon in world axes and the moon's own axes for its face.
- **The sky's disc is the sun's only** (#1798): the procedural sky draws a disc where the directional light comes
  from, and at night that light is the moon's shading (lifted to 25°, night-tinted): a dim blue blob where no body
  is, from the ground and from space (the "black sun"). The light is `LightOnly` while the sun is 3° up or lower,
  `LightAndSky` above. The moon is `MoonDisc`; the sun above the air is `atmosphere.gdshader`'s (`terrain/earth-far-view`).
  Windowed check: `--frame earth --skyshot sun|moon|light,altitudeM,out.png [--at E,N] [--date D] [--time H]
  --weather clear --occasion none` (`World/SkyShot`): the camera turned to the body, a picture, and a RESULT by the
  rule a body in view is drawn, one behind the Earth is not, and the light's own direction shows no disc unless it
  is the sun (stars hidden for the measure). Before #1798 it failed the space sun and both blobs.
- **`--terminatorcheck`** (#1845, `World/TerminatorCheck`, quick: `--frame earth --sun real --chunks fixture:straight
  --globe res://assets/globe --occasion none`): at the 2027 solstices and equinoxes, 04:00 and 15:30 UTC, the camera
  400 km and 20,000 km over Bern and over the evening terminator: the globe's subsolar point (`world_true_sun` through
  `Globe.Axes`) against NOAA's solar position algorithm written apart (`TerminatorCheck.Noaa`: 0.001-0.007°, under
  0.75 km), the sky's sun against it (0.0025° at 400 km, 0.010° at 20,000 km: the sun's true parallax from the
  camera), the ground's light against the sky's sun, the moon from the camera, the stars' sphere and true north
  (0.0000°), and `/time speed real` (the subsolar point 0.0833° in 20 s). Windowed (`--terminatorcheck shots`) also
  the drawn twilight from 20,000 km and the moon over the night side (`test_output/terminator_*.png`, `.csv`).
- **Online, one clock for everyone** (#452, `World/WorldClock`): the server always owns it, from its
  own settings at start (`StartHour`, `DayLengthMinutes`, `--time`), or, on a dedicated server, from `user://world_clock.cfg`
  where it stopped last (saved every 60 s, on `/time` and on exit; not when hosted from the menu,
  in test runs or with `--time`). It is three numbers, the hour `Hour0` at `Epoch` on `ClockSync.ServerNow` and the day
  length, sent to each joiner (`SendWorldTimeTo`) and on every `/time` (an admin's: it re-bases
  them). Every peer computes `Hour = HourAt(ServerNow)` each frame; **nothing is summed per frame**,
  so no drift, and a late joiner or a reconnect sees the same sky. A joiner's sky eases from its own
  hour to the world's in ~2 s (`DayNight._joinEase`) once `ClockSync.Synced`. Online the player's
  start hour and day length do nothing. Before #452 each client ran its own clock until an admin's
  `/time set`, and the sum drifted after it.
- **Game logic reads `WorldClock.CurrentHour`**, never `DayNight.Instance?.Hour ?? 12`: the server
  has no sky (the shared birds' night species, #143), and a `--systems` client neither. The bird
  chorus follows it; church bells and occasion tolls stay on the real wall clock on purpose (a
  24-min day would ring them every real minute).
- **Indoors** (#134): interior glass (`ps1_interior`, vertex alpha 0) is `world_sky * 1.3` plus a
  dark moonlit blue at night, so a room's windows show noon, sunset, blue hour and night. Rooms
  are lit by the hour (#388, `terrain/interior-light`: daylight and sun patches through the
  windows, lamps at night), so characters in them are too: `DayNight` keeps a second environment
  (the hour's tint × 0.86 by day, warm lamps at night) and `DayNight.EnvironmentAt(camera position)` hands it to
  any camera in the interiors' band under the terrain. It goes by where the **camera** is: the
  screen's camera (set each frame by `DayNight`) and each portal camera (`DoorPortals.Aim`, the
  untonemapped copies from `PortalEnvironmentAt`), so a
  player seen through an open door from the street is room-lit and one seen out of a door from
  inside is night-lit.
- **Open doors light the street at night** (`Interiors/DoorLights`): the four open doors nearest
  the camera each get a lamp just inside the doorway, behind the facade's plane, scaled by swing ×
  night. World shaders add `door_light(world_pos, n)` to `world_tint` (`door_light.gdshaderinc`,
  globals `world_door_light_0..3`: xyz lamp, w strength), lighting only what faces the lamp: the
  step, the ground in front, the jambs, not the facade beside the door. Avatars get the same light
  from a real `OmniLight3D` per slot (7 m reach), so stepping out of a lit room fades into the dark.
