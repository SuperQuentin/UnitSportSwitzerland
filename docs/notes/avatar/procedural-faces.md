# Procedural pixel faces (#657): a genome in the mesh, a state in instance uniforms

Replaced the #394 face atlas (16×16 pixel faces drawn from text). Plan: `docs/plans/procedural-faces.md`.

- **Genome** (`Avatar.Face.FaceGenome`, plain C#, unit tested in `FaceTests`): eye shape (8), size,
  spacing, height, iris, lashes, shine, brow (4) and its thickness and height, mouth (8) and its
  width and height, nose (4), blush, freckles, stubble, grid (16/24/32/40 pixels), an 8-bit blink
  seed. `Code` packs it into two whole numbers **below 2^24** that the face band carries as its
  **second UV** (`MeshScratch.FaceBand(..., code)`); the shader reads them through a `flat`
  varying. Decode with `int(round(x))`, **never `int(x + 0.5)`**: at 8 million a float32 steps by 1,
  so `x + 0.5` rounds up a whole number and every face draws from its neighbour's bits (that bug
  made every eye a slit).
- **Presets** = `Appearance.Face` (4 bits, saved and replicated, **append only**): 0-7 the #394
  faces' names (anime, calm, sharp, cute, freckles, stern, grin, stubble), 8-15 new (sparkle,
  sleepy, button, gloomy, cheeky, startled, dreamy, bold). `FaceGenome.ForSeed` makes faces up for
  creatures and crowds (#658); `BodyLook.Genome` overrides the preset. The blink seed comes from the
  look's colours (`HumanMeshBuilder.FaceSeed`), so passengers baked into one mesh blink apart.
- **Shader** `shaders/body/face.gdshaderinc` (included by `avatar.gdshaderinc`, finish 11): face
  space is 16 units across and down (the old atlas's pixels; x the viewer's right, y down),
  sampled at the centres of the genome's grid, so edges stay pixel-hard in every style. Eyes are
  ellipses mirrored onto the left half with a lid line, iris (the vertex colour = `Appearance.Eyes`),
  pupil, shine, lashes; brows are tilted arcs; the mouth is a curve bent by smile and opened by
  jaw (teeth, tongue), an "oh" ring, a cat ω or a smirk; then nose, dithered blush, freckles,
  stubble. Clear pixels are discarded, the head's skin shows.
- **Idle life is in the shader**: a blink every 3-6 s (sometimes double) and glances, on the seed and
  `TIME`. No CPU, nothing on the network, works in merged meshes (vehicles' drivers, a cabin full of
  passengers), which cannot have per-figure uniforms. Material uniform `face_idle` = 0 freezes it
  for stills (`--bodies`, `--models,<dir>`).
- **State** (`FaceState`: open L/R, gaze, smile, jaw, wide/pucker, brow, blush, squint, special eyes
  Cross/Hearts/Spirals/Happy/Wink): an ARKit/VRM-shaped vector, so mic (#659), webcam (#660) and VR
  (#661) can map onto it. Three `instance uniform vec4` (`face_eyes`, `face_mouth`, `face_mood`) on
  the figure's **own node**; the figure material stays one shared instance. `FaceAnimator.Step`
  eases towards a `FaceExpression`, adds a hit's grimace and a gaze, and writes a uniform only when
  its value changed by 1/32; `Reset()` when the node is replaced. `FaceAnimator.Apply` sets a state
  at once (previews, the ragdoll).
- **The walker** (`FootPlayer.Face`): mood from replicated state only (`Down` 1 crosses, downed pain,
  `Emote` → `FaceExpressions.ForEmote`, a radio dance happy), the grimace on `Flinch` (every peer
  plays it), eyes on a camera within 8 m, frozen past 30 m. No new network fields. The crash
  ragdoll has spiral eyes.
- A face on something new: draw a `FaceBand` with a genome's `Code` and the figure material. A new
  shape: a row in the shader's `FACE_EYES` / `FACE_MOUTHS` and the enum, both at the end.
- Check: `--avatars 2 <png> --bodies faces|expressions|expressions2|seeded --view 180 [--style ...]`
  (heads in a grid); model viewer category **Faces** (Preset, Expression, Seeded);
  `tools/emotenetcheck.sh` checks a remote copy's face shows its emote's expression.
- **Dancing faces (#728)**: dancing to music with no emote picked, the walker's face follows the CD's
  analysis (`Face.DanceFace.Target`, pure C#, `DanceFaceTests`; fed by `FootPlayer.DanceFaceNow` from
  `RadioGroove` and the drawn dance): calm dreams half-lidded, groove smiles, peak grins and opens
  on the hits, chorus sings "oh"/"ee" a beat each, a new section is a "wow", the bar's one lifts the
  brows; styles have a temper (rock snarls, hip-hop half-lidded smirk, chill eyes closed, folk
  beams, the rat dance hearts), dancers a personality from their seed (singer, winker, blusher);
  breaking concentrates, the freeze smirks, a near-silent song rests the face. `FaceAnimator.Step`
  takes a `FaceState` for it. Preview: `--avatars 2 <png> --bodies dancefaces --view 180`.
- **Bigger eyes (#728)**: the genome's eye size steps are 1.0 + 0.14 k (were 0.85 + 0.12 k).
