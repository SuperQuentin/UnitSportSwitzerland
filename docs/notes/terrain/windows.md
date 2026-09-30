# Windows

- **Windows**: `BuildingMeshBuilder` bakes facade UVs (metres along the wall, storey
  index) from the *triangle* normal; the shader draws the window grid from those. Storey
  height comes from GWR `GASTW` (69% coverage), else wall height / 2.9 m. Barns, garages,
  tanks and anything under 3 m opt out with uv.y < 0.
