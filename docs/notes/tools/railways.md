# Railways

- **Railways**: `tlm_oev_eisenbahn` carries gauge (`objektart` Normalspur/Schmalspur),
  `anzahl_spuren`, `zahnradbahn` (rack), `standseilbahn` (funicular), `ausser_betrieb`
  and `auf_strasse`. Rendered as a ballast ribbon plus real rail geometry
  (`RoadMeshBuilder.AppendRails`) at 1.435 m / 1.0 m gauge, doubled for two-track lines.
  Sleepers are a shader stripe, not geometry — at 0.65 m spacing they would cost tens of
  thousands of triangles per km for a few pixels.
