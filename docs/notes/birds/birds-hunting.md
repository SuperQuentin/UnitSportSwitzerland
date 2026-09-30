# Birds and hunting

- **Birds and hunting** (`src/Birds/`): `BirdCatalog` is ONE static table of **274 species**:
  every species that occurs *regularly* in Switzerland (Vogelwarte checklist and breeding atlas,
  IOC English names), i.e. 108 resident, 68 summer, 46 winter and 52 passage. Vagrants are left
  out. Each row gives length/wingspan, back/belly/accent colours, a `BodyPlan` (12 archetypes),
  a `FlightStyle`, `Habitat` flags, an altitude range, presence, an abundance weight, a flock
  size and, for the **20 game species** of JSG Art. 5, the open-season months. Anything else is
  protected. `BirdLife` (local and cosmetic like the traffic, ≤32 birds, removed past 240 m)
  samples a point 35–150 m out every 0.4 s and maps `TryGetCover` + altitude to a habitat with
  `HabitatAt`. Unmapped open ground is farm/meadow/alpine by altitude, because TLM has no
  farmland. It then draws a species weighted by abundance × real calendar month (`--birdmonth N`)
  × hour (owls at night) and spawns the whole flock. Woodland birds perch on real `.trees` tops,
  waterfowl swim at the water line, soarers circle, kestrels hover and swifts hawk. A bird
  closer than `5 + 18·√length` m flushes. `BirdMesh` builds each species once from `MeshScratch`,
  with the wings as separate meshes that flap about the shoulder. **Hunting**: the **shotgun**
  item (`ItemUse.Shoot`, `ItemId` 37–38 appended; Aim shoulders it at 50° FOV and shows a
  crosshair, Use spends a shell through `ItemController.Fire`) casts a cone that opens to 1.4 m
  across at 35 m, with hit chance fading from 30 to 55 m and a raycast so walls block. Every shot
  flushes everything within 150 m. The field journal (**J**, `user://birds.json` by species
  name) scores a game species in season by size and flight (+), a game species out of season
  (−100) and a protected one (−250). A player with no shotgun is given one plus 25 shells on the
  first run, existing saves included. Check: `<godot> --path . -- --birdcheck[,out.png]
  [--at E,N]` builds every mesh, surveys 600 points (habitat → species), auto-spawns for 6 s,
  then shoots a crow at 25 m and fires through the real item path. It writes to the real journal
  and inventory. Without terrain it still tests the hunt and skips the survey and spawning.
