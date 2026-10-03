# Avatars

- **Avatars** (`src/Avatar/`): procedural low-poly figures and a road bike, built from
  `MeshScratch` primitives (tapered tube, box, loft, skirt; the figure's body since #394:
  `body-shape`), so each is one surface and one draw call. `HumanMeshBuilder` poses a figure from a table of joint positions (Standing,
  Running, Cycling); `BikeMeshBuilder` uses real 700c geometry (0.99 m wheelbase, 0.27 m bottom
  bracket, saddle at 0.90 m) because a bike is a shape everyone knows. Walking and running are
  **one gait** in `HumanMeshBuilder.GaitRig`, solved from the constraint that a planted foot
  travels backwards at exactly the body's speed; the walk/run changeover is the duty factor
  crossing 0.5, which is what creates the flight phase. `Cyclist` combines them
  and splits the mesh three ways — frame, rider, and per-leg — so the cranks turn with cadence
  and the knees follow by a two-bone solve rather than keyframes. Preview with
  `<godot> --path . -- --avatars <seconds> <out.png> [--view deg] [--focus 0..4]` (in the saved
  visual style, or `--style`: the preview loads the settings since #311);
  `--crank <rad>` parks the cranks and `--stride <m/s>` lays one gait cycle out as a strip.
  Neither a crank's direction nor a foot's slip can be judged from a single frame.
  All limbs go through `Limb.Solve` (two-bone IK), never keyframes.
