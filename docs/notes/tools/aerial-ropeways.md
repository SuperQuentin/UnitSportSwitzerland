# Aerial ropeways

- **Aerial ropeways**: `tlm_oev_uebrige_bahn` -> `RoadClass.Cableway/Chairlift/SkiLift/RopeTow`,
  carried in the `.road` file but **not draped** — TLM digitises these along the *cable*, verified
  against our own heightfield: chairlifts run a median 11.9 m up, gondolas 14.6 m, and the
  Isérables tramway spans the Rhône valley 200 m clear of the ground. `RoadMeshBuilder` draws the
  cable as two ribbons crossed in a plus (a single flat one vanishes edge-on) and grows a tower
  from the terrain under every vertex — which is where the real pylons are, because that is where
  a cable changes direction. `Foerderband` and `Lift` are dropped: a conveyor and a building lift
  are not ropeways.
