# Teleport

- **Teleport** (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at
  construction. Flying camera gets ground + 220 m, a `CharacterBody3D` gets ground + 2 m and
  has its velocity zeroed and its placement pass re-armed.
- **Between bodies** (#2039, under `--frame earth`): `TeleportTo(body, lat, lon, label, land)` takes a place on any
  body (planetocentric latitude, east longitude; `land` metres east and north of it to arrive at). Off the body the
  anchor is on, or to the Earth from another body, it hands the world over first (`Core/BodyHandover`, shared with
  `/goto`): `WorldOrigin.Rebase` onto the body, then `ShiftTo` the place's own east-up-south frame (never the Earth's
  least-rotation `Tangent`, which reads the latitude off the old body's shape). The arrival is a `SpawnPoint` at world
  (0, 0) of that frame (the world-position constructor), settling on `IWorldGround` (`EarthWorld.OtherBody` off the
  Earth) with `StillLoading` while the Earth's leaves are made. The LV95 overload hands over to the Earth when the
  anchor is elsewhere, so every Swiss caller (the town search, `/tp`, the phone, adventure travel) works from the Moon.
  Refused with a reason (`RefusalFor`, the `Refused` toast): online (C3b, #1975), on the flat frame, and a walker onto a
  body with no ground. `--bodytravelcheck` (fly: Earth → Apollo 11 → Bern → Gale → Geneva; on foot: Earth → Artemis
  Base → Bern; the refusals).
- **The place search by body** (`Core/PlaceSearchUi`, #2039): a stepper over the list (‹ Moon ›; LB / RB, Page Up /
  Page Down, a click or the VR ray) opening on the body you are on. The Earth lists `PlaceIndex`'s towns; every other
  body with places lists `Terrain/Bodies/BodyPlaces` (the Moon's colonies, sites and outposts from its tables, landing
  past a colony's hangar down its road; a committed table of landing sites and great features for Mercury, Venus, Mars,
  Phobos, Deimos, Io, Europa, Ganymede, Callisto and Titan) beside `Core/BodyMapView` (the body's equirectangular photo,
  `MoonDisc.MapPath` / `SkyBodies.MapOf`, else its tint on a 30° grid; dots, a ring where you are; a dot clicked travels
  there, anywhere else to that spot). On another body M opens this search, not the adventure map (the Earth's).
