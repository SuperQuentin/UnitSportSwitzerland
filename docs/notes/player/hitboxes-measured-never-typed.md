# Hitboxes are measured, never typed

- **Hitboxes are measured, never typed** (`Avatar/MeshBounds`, `Player/Hurtbox`). Movement keeps
  its capsule (a rigid 11 m box would snag every slope of the 1 m lattice), but every drawn
  machine — mounted player or parked `VehicleBody` — also carries a **`Hurtbox`**: an `Area3D`
  fitted to its mesh bounds, parented to the VISUAL so it banks and flips with it, alone on
  physics layer 8 (`Hurtbox.Layer`), monitorable, not monitoring — no movement changes. A shot that
  wants it sets `CollideWithAreas = true` with `Hurtbox.Layer` in its mask and resolves the hit
  with `Hurtbox.BodyOf` (combat, PR #6, needs that one-line change to hit wings and rotors).
  `Rideable.ParkedBox` defaults to the parked mesh's bounds (`Measured`, once per kind; the
  helicopter leaves its "Rotor" out; the plane keeps a documented fuselage-only box, checked to lie
  inside its mesh), and traffic units take their mesh's own AABB. Before: bike parked box 1.10 m
  tall for a 0.91 m bike, traffic car/van boxes 15/20 cm over the roof, carriages 30 cm short.
  Check: `<godot> --path . -- --hitboxcheck [--at E,N]` — per mount: drawn bounds, capsule, parked
  box, hurtbox; live rays through a wingtip and a rotor rim; and with `--at` in a town, a ray from
  outside at up to 5,000 faces of the real `.bldg` tiles through `ChunkNode.BuildingShape` (the
  building collision is one-sided `ConcavePolygonShape3D` with the render's raw winding — this is
  the raycast verification the bridge-deck gotcha asked for; not yet run on a region with
  buildings).
