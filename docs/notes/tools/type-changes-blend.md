# Type changes blend

- **Type changes blend**: swissTLM3D splits a road wherever any attribute changes, so a
  widening or an asphalt-to-gravel change is two features sharing an endpoint, and ribboning
  them independently leaves a step — a shoulder sticking out of the carriageway plus a hard
  colour seam. `RoadMeshBuilder.FindTypeJoins` indexes segment endpoints per tile, and where
  exactly **two** meet (three is a junction, already covered by its polygon) pulls both to the
  *mean* width and colour at the shared vertex, then eases each back to its own over 5–22 m.
  Taking the mean is what closes the step: tapering each side toward the other's value still
  arrives at two different numbers. Smoothstep, not a linear ramp — a straight ramp leaves a
  crease where the rate of change jumps. Roughly 94 such joins per 16 tiles.
