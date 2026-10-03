# Cartoon ink outline on figures (#394)

- In the Cartoon style `StyleKit.Configure` gives the figure material (`MaterialRole.Figure`) a
  next pass, `shaders/figure_outline.gdshader`: the mesh again, back faces only, pushed 12 mm
  out along its normals, unshaded ink. Other styles get no next pass (set back to null on a
  style change).
- It needs normals, which `MeshScratch` writes only for the lit styles' figures
  (`MeshDetail.High`, Cartoon's). Lofts get round normals out of each ring's middle, so the line
  closes over the trunk's and head's facets; boxes (hands) keep flat normals and may show a small
  gap at a corner.
- A part drawn without normals in the same mesh (a bike frame under its rider) carries
  `MeshScratch.NoNormal`, +Z once the mesh is turned: the shader leaves those vertices where they
  are, so their back faces stay hidden behind their front ones and no ghost shape shows.
