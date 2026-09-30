# Watercourses

- **Watercourses**: `tlm_gewaesser_fliessgewaesser` -> `Watercourse`/`DryChannel`/`Bisse`, draped
  like roads (their Z sits on the ground — median offset −0.14 m) and meshed by
  `WaterMeshBuilder` so they take the water material instead of being drawn as narrow blue roads.
  This is what puts water in the mountains at all: the cover raster only finds water wide enough
  to register on the 2 m lattice, which in alpine terrain is almost none of it. `Druckstollen`
  (a pressure tunnel, 232 m *underground*) and `Druckleitung` (a penstock ~5 m above ground) are
  excluded — they are hydro plumbing, and drawing them would run rivers through mountains — as
  is anything whose `verlauf` is `Unterirdisch`, which is 42% of the channels around Riddes and
  would otherwise put streams down the middle of village streets.
  **The raster wins over the lines**: `WaterMeshBuilder` drops any channel that mostly runs
  inside already-mapped water, because the Rhône is in this dataset as a centreline like every
  other river and drawing it laid a 2.5 m creek down a 50 m channel.
