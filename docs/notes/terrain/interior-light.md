# Interior light: rooms lit by the hour, in every style (#388)

Rooms float 3 km under the terrain (`InteriorManager.InteriorBaseY`), out of Godot's sun, so they
light themselves, unshaded, from the same day/night globals as the street (`day-night`).

- **One body, three wrappers.** `shaders/body/interior.gdshaderinc`; `ps1_interior` (retro finish
  behind `retro`), `cartoon_interior` (`INTERIOR_CARTOON`: the light in soft bands, a pastel
  grade) and `real_interior` (`INTERIOR_REAL`: Plaster001 on walls and ceilings via
  `style_tinted`, procedural boards on wooden floors, i.e. floor colours with red > 1.25 × blue).
  `StyleKit` maps `MaterialRole.Interior` per style and sets `light_gain` (PS1 1.0, Cartoon 1.15,
  Realistic 1.35: a lit style's sunlit street is brighter than its albedo).
- **The light** (`room_light`): `world_tint × ambient_day` (0.42) everywhere, plus per window to
  the outside: daylight (`mix(world_tint, world_sky × 1.3, 0.35)`, more on what faces it, falling
  off over a few metres, capped) and the **sun's patch**: a fragment is in it when the ray from it
  toward `world_sun_dir` passes through the window's rectangle. Plus a ceiling lamp per room,
  warm (`lamp_color`), on with `world_night`, and all day in a room with no window (cellars,
  halls). No shadowing beyond "a light reaches only its own room's box".
- **The table** (`Interiors/RoomLights`): a float `ImageTexture` per interior, row 0 = (first,
  count) per floor, row 1 = four texels per light (centre + kind; inward normal and half size, or
  reach and always-on; room rectangle; floor and ceiling y), interior-local. A fragment walks only
  its own floor's list. A room spanning storeys (a nave) is listed on each.
- **Per interior material.** `InteriorNode.Create` makes its own `StyleKit.Material(Interior)`
  (restyled like the others), sets the table and uses it for the room mesh (`MaterialOverride`),
  leaves, lock doors and a church's figures. `interior_frame` (world → interior-local) follows
  the node on every transform change, origin shifts included.
- **Characters indoors** (standard materials): the indoor environment's ambient is the hour's
  tint × 0.86 by day, the lamp at night (`DayNight.RoomDaylight`). The style's sun stays visible
  with the camera indoors; the room's shell shadows it, except at the windows.
- **Door portals are tonemapped once** (Cartoon/Realistic): portal cameras use
  `DayNight.PortalEnvironmentAt`, copies of the outdoor and indoor environments with a linear
  tonemap, no glow, no adjustments and exposure 0.5 (`PortalExposure`), into `UseHdr2D`
  viewports; `door_portal.gdshader` reads the picture as linear and multiplies by `view_gain` (2,
  or 1 when there is no `DayNight`, as in `--portaldemo`). Before, the street seen from a room at
  noon was night: the sun was hidden whenever the screen's camera was indoors.
- **Check by eye:** a `--shot-queue` of the style-shots house (`styles/commands`) inside and out
  at 14:00, 17:30 and 23:00 per style.
