# Road markings

- **Road markings**: swisstopo publishes NO lane/marking dataset (`tlm_strassen_strasseninfo`
  is junctions and POIs, not lanes). Markings are therefore *inferred* at build time from
  width class + `belagsart` + `richtungsgetrennt` into a `MarkingStyle`, baked into uv2.x,
  and drawn by `ps1_road.gdshader` from uv = (metres along, lateral in [-1,1]).
  Divided carriageways deliberately get edge lines and no centre line.
