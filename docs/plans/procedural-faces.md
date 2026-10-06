# Procedural animated pixel faces (#657)

Status: phase 1 (renderer + idle life) built in #657 (PR #664); follow-ups #658-#661 open.
Note: `docs/notes/avatar/procedural-faces.md`.

**Built differently from the plan below:**
- The genome is in the **mesh** (the face band's UV2, two 24-bit whole numbers), not in instance
  uniforms. Many vehicles bake their driver or passengers into one mesh (cars, the A320 cabin,
  the steamer), and one node cannot carry a face per figure.
- **Blinking and glances run in the shader** (seeded per face, on `TIME`), not in an `IdleDriver`.
  That costs no CPU and works in merged meshes too. Instance uniforms carry only the driven state,
  in three vec4s.
- There is no driver interface yet. `FaceAnimator.Step` takes an expression, a look target and a
  pain flash; the mic and tracking drivers will add their inputs there (#659-#661).
- The eyes look at a camera within 8 m, not at the nearest player.
- The probe is `--bodies faces|expressions|expressions2|seeded` in the avatar preview, not a new
  `--faces` flag. `tools/emotenetcheck.sh` checks the remote face.

## Context

Today every figure has one fixed pixel face. `src/Avatar/FaceAtlas.cs` draws 8 faces of 16×16
pixels into a 64×64 atlas, and `Head.Face` in `HumanMeshBuilder.Body.cs` lays them on a UV band
on the head (finish 11 in `shaders/body/avatar.gdshaderinc`). Nothing moves: there is no blinking,
no gaze, no expression and no talking. Other creatures (chapel congregation, the rat, birds, the
pigeon player) have faces built from boxes or no face at all.

The goal is one procedural 2D face system to replace all of them. A face is a small set of shape
numbers (its **genome**) plus an animated **expression vector**. Anything can drive that vector
later: idle life, emotes, damage, a microphone, a webcam or VR face tracking. Choices made:
- **Look:** pixel art in every style, at a finer grid than 16×16 (default 32×32, set per creature).
- **First PR:** the renderer plus idle life (blinking, gaze, expressions on emotes and damage).
  Mic, webcam and VR tracking are later PRs, and the design leaves room for them.
- **Targets:** every creature in the end. The first PR covers human figures; others follow.

## Technology landscape (and what we take from it)

| Need | What exists today | Our pick |
|---|---|---|
| Parameter standard | ARKit 52 blendshapes; FACS action units; VRM 1.0 expressions (blink, aa/ih/ou/ee/oh, happy, angry…); Live2D params | A **compact ARKit/VRM-shaped vector** (about 12 floats), so any source maps onto it |
| Drawing a 2D face | Live2D Cubism (proprietary), Inochi2D (open source), Spine, sprite swaps, **SDF shapes in a shader** | **SDF in the existing figure shader**, sampled on a quantised pixel grid: crisp pixel art, no textures, every value can animate |
| Idle life | Blinks at random intervals, small eye jumps (saccades), look-at targets | Local and deterministic, so nothing goes over the network |
| Audio lip sync | Loudness → jaw (cheap); formant/MFCC vowel classifiers (uLipSync, OVRLipSync); offline Rhubarb | Later: Godot `AudioEffectCapture` + `AudioEffectSpectrumAnalyzer` → jaw plus one of 5 VRM vowels; **replicate the parameters, not the audio** |
| Webcam tracking | Google **MediaPipe Face Landmarker** (gives 52 ARKit blendshapes, runs live on the CPU); OpenSeeFace; XR Animator / VSeeFace send them as **VMC/OSC** | Later: a VMC receiver, shared with the planned `MocapReceiver` (`docs/plans/movie-studio.md`) |
| VR face tracking | OpenXR `XR_FB_face_tracking2` (Quest Pro), `XR_HTC_facial_tracking`; Godot 4.3+ **`XRFaceTracker`** (unified blendshapes) | Later: read `XRFaceTracker` and map it onto our vector |

Underlying idea: **every source writes into one `XRFaceTracker`-style vector and the renderer only
reads that vector.** New inputs then need no changes to the renderer.

## Design

### 1. Data (`src/Avatar/Face/`)
- `FaceGenome` (readonly record struct, static per creature): eye shape (round/almond/droopy/
  sharp/button), eye size, spacing and height, iris size, lashes, highlight, brow shape and
  thickness, mouth width and shape, nose (none/dot/line), blush, freckles, stubble, grid resolution.
  - `FaceGenome.Preset(int face)` rebuilds the 8 current atlas faces, so saved `Appearance.Face`
    indices and the 4 network bits still mean the same thing.
  - `FaceGenome.ForSeed(uint)` gives variations for NPCs and creatures.
  - `Pack()` turns it into 2 vec4 for the shader.
- `FaceState` (struct, per frame): eyeOpenL/R, gazeX/Y, browL/R, browAngle, jawOpen, mouthSmile,
  mouthWide/Pucker (vowel), squint, plus a mood tint (blush). Packed into 3 vec4.
- `FaceExpression` presets (neutral, happy, sad, angry, surprised, pain, sleepy, dizzy): each is a
  partial `FaceState`, blended by weight.

### 2. Renderer (shader)
- New `shaders/body/face.gdshaderinc`, included by `avatar.gdshaderinc`. Finish 11 calls
  `face_px(UV, genome, state)` instead of `texture(face_atlas, UV)`.
  - `uv = (floor(UV * res) + 0.5) / res` quantises to the pixel grid, so edges stay pixel-hard.
  - Eyes: ellipse or superellipse SDF, cut by an upper and lower lid line moved by eyeOpen. The
    iris is a circle offset by gaze and clipped to the eye white. Highlight pixel, lashes.
  - Brows: thick arc segments, raised and tilted by the brow values.
  - Mouth: a quadratic curve whose bend comes from smile and whose open height comes from jaw
    (an inner dark ellipse plus a tongue). Width comes from genome × wide/pucker.
  - Anything transparent is discarded (as today); the iris uses the vertex eye colour (as today).
- Per-figure values travel as **`instance uniform vec4`** (5 of them). The material stays one
  shared instance (`perf-shared-materials`), and the existing pattern
  (`SetInstanceShaderParameter` in `src/Terrain/PhotoLayer.cs`, `ChurchStage.Disco.cs`) is reused.
  The mesh is never rebuilt for a blink.
- `Head.Face` in `HumanMeshBuilder.Body.cs` passes `Rect2(0,0,1,1)` to `MeshScratch.FaceBand`
  instead of `FaceAtlas.Uv(face)`. Check the band's aspect (9 columns × 5 rows over 110°) and
  correct it in the shader so circles come out round.
- `StyleKit.Configure` (line 342) stops setting `face_atlas`. `FaceAtlas.cs` keeps only `Count`
  and `Name` (renamed `FaceGenome.Presets`), and the atlas drawing is removed once the presets match.

### 3. Animation (`FaceAnimator`, a node on each figure root)
- A list of `IFaceDriver { void Write(ref FaceState s, ref float weight, double now); }`, blended
  in order:
  - `IdleDriver`: blinks at random intervals (seeded by rider id, timed with `GameClock.Now`), with
    an occasional double blink; small eye jumps; looks at the nearest player or camera within 8 m.
  - `ExpressionDriver`: an emote (`FootPlayer.DanceId`, `EmoteTable`) maps to an expression; taking
    damage flashes pain; sleeping or KO shows closed eyes. These are already replicated, so remote
    peers see the same thing with **no new network fields**.
  - Later drivers: `VoiceDriver` (mic), `TrackerDriver` (`XRFaceTracker`/VMC).
- It pushes instance uniforms **only when the packed values change**, with no allocation per frame
  (static `StringName`; `perf-no-per-frame-allocations`). Figures more than about 30 m away freeze
  their face, a cheap level of detail.
- Masks and the pumpkin head (`HairCover.Head`) still hide the face, as today.

### 4. Model viewer and preview
- `[Showcase("Faces")]` entries in `HumanMeshBuilder.Showcase.cs`: the 8 presets, 8 seeded
  genomes, and every expression on one face.
- `AvatarPreview --bodies faces` keeps working (it loops over the presets).
- Add a `--faces` probe that renders one face through blink, smile, jaw and gaze steps into
  `test_output/faces/`.

### 5. Later PRs (one issue each, listed in the plan, not built now)
1. **Other creatures (#658):** a generic `FaceDecal` quad (or a band on their own mesh) with the same
   shader include, for the chapel congregation (replacing the `Eyes`/`Mouths` box grids in
   `InteriorMeshBuilder.Congregation.cs`), the rat pastor, birds and the pigeon player (round eyes,
   no mouth, `res` 16), and possibly characterful vehicles.
2. **Mic lip sync (#659):** `AudioStreamMicrophone` → a bus with `AudioEffectCapture` → loudness plus
   3 formant bands → jaw and vowel. Sent as 2 bytes at 15 Hz, unreliable, on `FootPlayer`.
   Needs a tier-2 (`tools/test.sh net`) run and a settings toggle; the mic is off by default.
3. **Webcam (#660):** a VMC/OSC receiver (shared with movie-studio's `MocapReceiver`) fed by
   XR Animator / MediaPipe, mapping ARKit blendshapes onto `FaceState`, replicated like the mic.
4. **VR (#661):** `XRFaceTracker` from the OpenXR vendor face-tracking extensions.

## Critical files
- `shaders/body/avatar.gdshaderinc` and the new `shaders/body/face.gdshaderinc`
- `src/Avatar/FaceAtlas.cs` → `src/Avatar/Face/FaceGenome.cs`, `FaceState.cs`, `FaceAnimator.cs`,
  `IdleDriver.cs`, `ExpressionDriver.cs`
- `src/Avatar/HumanMeshBuilder.Body.cs` (`Head.Face`, `FaceMark`, `Appearance`)
- `src/Styles/StyleKit.cs:342`; `src/Items/InventoryUi.cs:651,666`; `src/Avatar/AvatarPreview.cs:649`;
  `src/Avatar/OutfitCheck.cs:76`
- `src/Avatar/HumanMeshBuilder.Showcase.cs`
- Wherever figure MeshInstances are created (player `RefreshVisual`, NPCs, GPX ghosts) gets a
  `FaceAnimator`. Check whether any crowd uses MultiMesh: instance uniforms do not work there, and
  it would need `INSTANCE_CUSTOM` instead.

## Verification
- `dotnet build UnitSportSwitzerland.csproj`; unit tests for `FaceGenome.Pack` round trip, the
  preset table and the blink schedule's determinism.
- `--bodies faces` and `--models,test_output/models` (Faces category) in ps1, cartoon and realistic.
  Compare against today's 8 faces and send screenshots along the way.
- The `--faces` probe: frames show eyes closing, the mouth opening and the gaze moving.
- `tools/test.sh quick`. Run `tools/test.sh net` because emotes drive faces on the remote peer:
  check that a second client sees the emote expression and that blinking runs there too.
- Perf: profiler with 50 NPC figures; no allocations per frame from `FaceAnimator`, and uniform
  writes only on change.
