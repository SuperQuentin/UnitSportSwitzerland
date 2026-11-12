# Occasions

- **Occasions** (`src/Occasions/`, #18): time-limited themes and events — Halloween (1 Sep–31
  Oct), Christmas (1 Nov–31 Dec) and UFO sightings (first Friday nights, `ufo-sightings`) so far, built to carry an Olympics or a community event.
  *Policy* (schedule, facets, priority, `allowClientOptOut`) is `OccasionEntry`, built-in defaults
  merged under `user://occasions.json` by id; *content* is an `Occasion` subclass with the same
  id (an id with no class runs as a contentless `GenericOccasion`). Schedules are `MM-DD`
  (recurring, may wrap over new year) or `YYYY-MM-DD` (one-off); the instance key
  (`christmas-2026`) is the year the window **opened**, so a new-year-spanning window is one
  instance. `OccasionManager` at `World/Occasions` on both sides: the dedicated server reads
  **its own local clock** each minute and replicates the running set (`SetActive`, and
  `SendTo` on join); offline the client is its own authority. `Active` is that set filtered
  by the player's per-occasion preference (`GameSettings.OccasionPreferences`: Auto / Off /
  Always) — Off strips only the **cosmetic** facets, never loot or the hunt, and not at all
  when the server locked the occasion. Everything reacts to `OccasionManager.Changed`.
  Rendering reaches every world shader through `shaders/world_occasion.gdshaderinc` and five
  globals `DayNight` writes each frame (`world_snow`, `world_mist_color/density/top`,
  `world_lights`); all zero is a strict no-op, and `DayNight` takes its colour grade from
  the top occasion's `Atmosphere`, and under the fixed sun (`--sun fixed`, check runs) its
  sunrise/sunset/noon too, reducing exactly to the old 6/18/62° day without one; the real sun
  (#1430) has the date's own day. Overrides: `--occasion <id|none>` (repeatable; replaces the calendar for the
  session), `--date YYYY-MM-DD`, and `/occasion list|start|stop|auto` in chat or the server
  console.
  **Content hooks** (all on `Occasion`, all no-ops by default): `Decorate`/`PlaceHunt` per tile
  via `OccasionDecor`, which listens to `ChunkManager.TileFurnished`/`TileUnloaded` (fired beside
  the `DoorIndex` calls) and parents MultiMeshes to the tile's own node, so they unload with it;
  every placement is hashed (`OccasionHash`) from building keys and world-anchored cells, never a
  shared random stream. Props use `ps1_prop.gdshader`, where **vertex alpha is the glow mask**
  (dark by day, lit at night). `Flocks` (bats/crows/wisps, `OccasionCreatures`), `Ambience`
  (`OccasionAmbience`, sounds in `OccasionSounds`), `Jingle` (menu, `Audio/ChipTune`), `Treats`
  (→ `LootTables.Seasonal`, one extra roll drawn **last** so no-occasion loot is bit-identical),
  `HuntReward`, `Hat` (→ replicated `FootPlayer.HeadwearId` via `OccasionHats`; a worn hat item,
  `Inventory.Worn`, wins). Towns come from `OccasionTowns`: `places.json`, else
  `ProceduralWorld.VillageCentres()` for the generated world, found round a tile's middle by its place in the
  world (#1729). `TileContext` works in the tile node's whole frame (`Frame`): on the earth world a leaf's stand-in
  tile is turned (~28°) and spans less than its square, so heights, cover and LV95 go through the frame, a place
  is on the tile when the ground names that tile there (`TileAtWorld`, `Holds`, `InTile`), and spots are read
  through the node's `GlobalTransform`. The earth world furnishes leaves within 900 m only: `--huntcheck` goes
  to the nearest town after 10 s without a spot. The hunt is taken with the
  **gather hold** (G), not E — the spots stand beside doors, where E means "go in" — and claims
  are local (`user://occasions/claims.json`, per instance), like `Gathering` and the inventory.
  `ItemId` 37/38 are reserved by the bird-hunting PR (#7); occasions own 39-49. Festivals (#1269) are occasions with the gameplay
  `Games` facet, two more schedule tokens (`04-mon#3`, `easter-46`) and their own service: `festival-framework`.
  **Christmas** is almost entirely shader-side: `world_snow` whitens up-facing terrain, roofs,
  crowns and road *edges* (carriageways are ploughed), `world_lights` draws coloured bulbs along
  the eaves in `ps1_building` (no geometry) and lights more windows; falling snow is one static
  mesh of 4k quads that `snowfall.gdshader` drops and wraps round the camera in **world**
  space, offset by `world_origin_offset` so a floating-origin shift does not reshuffle the flakes
  (the 60 m box divides its 9.6 km period, `core/floating-origin`) (`OccasionPrecip`, zero CPU per frame; the shader draws nothing for a camera
  under -2000 m, i.e. indoors, per camera so a door portal looking out still shows it). None above the cloud layer (#1637, `SnowCeiling`): the game has no cloud height, so its base is 2,000 m over the lowest ground round the camera (sampled twice a second, eased, like the mist top) and the snow fades across 1,800-2,200 m over it; the valley thinning (45 % under 700 m) went from the shader's `cam.y` to there. Both read the camera's altitude through `WorldOrigin.AltitudeOf`, right in the flat world, in space and under `--frame earth`. Crossing it logs `[snow] above/under the clouds at <alt> m, ground <alt> m`. While it snows a **cloud deck** hangs there (#1643, `shaders/cloud_deck.gdshader`): a slab of fog 1,900-2,400 m over that ground, one full-screen pass (like `underwater.gdshader`) fogging each pixel by its view ray's length inside the slab up to the depth buffer, so it hides the sky from under, the ground from over, and whites out inside; 40 km round the camera with a soft edge, full at snowfall 0.3 and over, faded out from 12 to 20 km up (`SnowCeiling.DeckStrength`), none for a camera indoors. Its heights are pushed relative to the camera, so no world Y is read: right in the flat world, in space and under `--frame earth`. Props are a 12 m fir per town
  (`TreeSpot` nudges it off roads and doorsteps; tree and gifts agree by recomputing it) with
  five hunt gifts under it, plus a gift at ~1 door in 20.
