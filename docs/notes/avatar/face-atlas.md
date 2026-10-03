# Pixel faces (#394): an atlas drawn as text, the iris keyed for the eye colour

- `Avatar.FaceAtlas` draws a 64×64 `ImageTexture` at startup from text: 4×4 cells of 16×16, each
  face the left half of a cell mirrored onto the right. No imported asset. Keys: `k` line, `w` eye
  white, `i`/`d` iris and its dark, `h` highlight, `m` mouth, `b` blush, `n` nose, `f` freckle,
  `s` stubble. Eight faces: anime, calm, sharp, cute, freckles, stern, grin, stubble (index =
  `Appearance.Face`, append only: it is replicated).
- The head's face is a band over its front (`MeshScratch.FaceBand`, the one primitive with UVs:
  `MeshScratch` writes a UV stream only once one is drawn, (0, 0) elsewhere), 4 mm proud, from brow
  (0.178) to chin (0.010), 145°→35° round the head. Its vertex alpha is finish id 11
  (`Finish.Face`, `FaceAtlas.FinishId`); `shaders/body/avatar.gdshaderinc` samples `face_atlas`
  (nearest), discards clear pixels so the head's skin shows, and paints **magenta pixels**
  (`(k, 0, k)`) in the vertex colour at their shade: the iris is keyed, so the eye colour is the
  figure's own (`Appearance.Eyes`) whatever the face.
- `StyleKit.Configure` sets `face_atlas` on the figure material in every style. Only the figure
  material (`HumanMeshBuilder.FigureMaterial`) draws faces: **every mesh with a figure in it must
  use it** — under the plain `Material()` the band shows as a rectangle of eye colour.
- A new face: add a row set to `Faces`, keep the eyes on rows 5-9 (the glasses sit at 0.100 up
  the head) and the mouth on rows 11-13, check it with `--bodies faces`.
