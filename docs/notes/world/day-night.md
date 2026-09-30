# Day/night

- **Day/night** (`World/DayNight`): four **global shader uniforms** declared in `project.godot`
  `[shader_globals]` — `world_sun_dir`, `world_tint`, `world_sky`, `world_night` — written once a
  frame reach every `ps1_*` world shader (each multiplies by the tint just before its Bayer
  quantise and fogs toward the sky) with no material touched. The environment background and
  ambient follow the sky, because avatars and vehicles are standard materials lit only by that.
  Simple Swiss-summer sun (up 6h, 62° at noon, down 18h); below the horizon the shading follows a
  moon so mountains keep a lit side. **Tint and sky are authored as seen and converted
  `SrgbToLinear` before upload** — the shaders multiply linear values, and unconverted night's 0.13
  displayed as 0.40. Buildings light far more windows at night and those glow through the dark.
  Settings → Time of day (start hour, day length, default 24 min, 0 = stopped); `--time <h>` fixes
  the hour for one run.
- **Indoors** (#134): interior glass (`ps1_interior`, vertex alpha 0) is `world_sky * 1.3` plus a
  dark moonlit blue at night, so a room's windows show noon, sunset, blue hour and night. Rooms
  are lit, so characters in them are too: `DayNight` keeps a second environment (room daylight by
  day, warm lamps at night, never dimmed) and `DayNight.EnvironmentAt(camera position)` hands it to
  any camera in the interiors' band under the terrain. It goes by where the **camera** is: the
  screen's camera (set each frame by `DayNight`) and each portal camera (`DoorPortals.Aim`), so a
  player seen through an open door from the street is room-lit and one seen out of a door from
  inside is night-lit.
- **Open doors light the street at night** (`Interiors/DoorLights`): the four open doors nearest
  the camera each get a lamp just inside the doorway, behind the facade's plane, scaled by swing ×
  night. World shaders add `door_light(world_pos, n)` to `world_tint` (`door_light.gdshaderinc`,
  globals `world_door_light_0..3`: xyz lamp, w strength), lighting only what faces the lamp: the
  step, the ground in front, the jambs, not the facade beside the door. Avatars get the same light
  from a real `OmniLight3D` per slot (7 m reach), so stepping out of a lit room fades into the dark.
