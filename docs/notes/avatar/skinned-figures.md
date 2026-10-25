# Skinned figures: built once per look, posed by bones (#1202)

## Rule
- **A figure that moves is a `SkinnedFigure`**, never a mesh rebuilt per pose. Its look
  (`FigureLook`: palette without the wind, legs, helmet, hat, body/head halves, full-face, pointing
  hand, pedalling legs, smooth style) is built once at the standing rest rig
  (`HumanMeshBuilder.Skin(look)`, cached and shared by every figure of that look). A pose computes the
  same `Rig` the builders always computed and only sets bones:
  `HumanMeshBuilder.PoseStride / PosePosed / PoseFalling / PoseSwim / PoseJoints / PoseDriver /
  PosePilot / PoseRider / PoseCyclist`, and `SeatedFigure.Pose`.
- `Show(look)` when the look changes (clothes, hat, restyle #311, a finger out to point #889); it
  is a dictionary lookup when it does not. Nothing else builds a mesh.
- The bones are a rendering-server skeleton of the figure's own (`RenderingServer.SkeletonCreate`),
  attached to its instance, not a `Skeleton3D`: a limb's joint ring is stretched across the bend
  (`LimbLoft`'s mitre), which a bone's position/rotation/scale cannot hold; the server takes any
  affine matrix. Only bones that moved are sent.
- **Copies move by the same bones**: `figure.Share(copy)` (the own body's shadow twin, a doorway's
  ghost, a driver's head mesh on its body's bones). A copy that is itself a `SkinnedFigure`
  re-attaches when it re-enters the tree (a `MeshInstance3D` attaches its own, none, on enter tree,
  and a script's notification runs after the engine's: `_Notification`, not `_EnterTree`).
- **What measures or copies a mesh asks for the posed one**: `SkinnedFigure.BoundsOf(v)`,
  `MeshOf(m)` / `Baked()` (CPU skinned, a new mesh per pose, never per frame), `Still()` for the
  movie recorder. A skinned mesh's own AABB is its rest pose: a seated driver counted as a
  standing figure at the car's origin would make the hitbox and hull taller. Culling is fine: the
  server bounds the posed bones.

## The bones (`HumanMeshBuilder.Skin.cs`)
- The frames each part is already drawn in, from the rig: the trunk's rings every quarter of the
  spine (`Torso.Frame`, 17), each limb's upper bone, mitred joint ring and lower bone (side carried
  from the shoulder line as `LimbLoft` carries it), the hands (`HandFrameOf`, the pointing one
  along its own way), the feet (`Boot`: scaled along the foot), the head (scaled with its length,
  as `Head.K`), the neck tube's own bone, a collar bone along the neck for neckwear.
- Each vertex is weighed by the part it is drawn for: the `RestMap` scopes (`TrunkRest`,
  `ArmRest`…) carry a part number, and `MeshScratch.Weigher` turns part and rest position into up
  to four bones. Trunk: the two rings either side of its height; a box or ring on it at its
  middle (a buckle across two rings would stretch with the spine). Limb: before the mitre, on it,
  after it, past the end (hand, foot); a tube or box on a limb by its middle (`LimbBand` never
  crosses a joint, so only the limb's own loft bends there).
- **Skirts** (#251, #671): one bone per point of the drape's rings below the waistband
  (10 x `DrapeSides`), each moved out as far as `HangSkirt` pushes it round the hips and legs
  (`SkirtExtents` is `HangSkirt`'s own rule), on an axis to the hem blown downwind. A tier (a
  dress's petticoat, a ruffle's second) has its own 240, and its points blend the two as `Tier`
  does. Only a look with a skirt carries those bones (`FigureSkin.BoneCount`). The hem's ripple
  is in the figure shader (`skirt_flutter`, CUSTOM1: offset and index), and so is the floor the
  skirt never drops under (`skirt_floor`); the CPU sets both on change.

## Why
The figure was rebuilt and re-uploaded for every pose (`perf-pose-mesh-cache`): ~0.8 ms and
180 KB of garbage a rebuild on a desktop, every frame for anyone walking, falling, dancing,
swimming, steering or pedalling. On the Quest (#1112) the frame rate quadrupled when the tracked
hands stopped the own figure's arms moving. Measured before/after: see the PR.
- **On the Quest 2** (debug exports, 0cf043cf vs 5946bf42, `tools/quest-measure.sh`): offline it is the
  same, with no shader errors on the Mobile renderer. The kart street scene (`--at 2500300,1118450 --heading 207
  --seat kart`) runs at 42.2 fps, 17.8 ms GPU before and 42.4 fps, 17.8 ms after; the title screen at 72 fps,
  8.2 ms both. With bots it is not measured yet: a Quest cannot stay on a server (#1218).

## Same look, checked
- `--skincheck` (quick tier, ~70 s): every look (builds, hair, hats, helmets, every garment)
  against every pose (gait speeds and phases, fixed poses, item arms, VR hands, falls, swims,
  dances, emotes, drivers, rider, cycling, a ragdoll): the old builder's mesh at the pose against
  the skinned one deformed on the CPU, vertex to surface. Bounds p99 8 mm, max 15 mm.
  `--skincheck <look> <pose>` prints the worst vertices of one.
- What is left inside the bounds: a round primitive's facets turned about its own axis (a
  `Tube`'s ring takes its orientation from world up; the check turns them with their part, the
  game does not, and it does not show), fixed-size hat parts on a head the old builder shrank with
  its length (a shouldered gun tips the head), a skirt strap between its rings in a sprint.
- Accepted differences: a slit skirt's slit stays cut in the cloth when seated (the old drape cut
  it where the pose turned it); a limb band ending at the elbow is mitred with the arm.

## Migrating old code
- `grep -rnE "BuildStride\(|BuildPosed\(|BuildFalling\(|BuildSwim\(|BuildJoints\(|AppendJoints\(|BuildCyclist|DriverBody\(" src`
  in a `_Process`, `Step`, `Animate*` or `Apply*Pose`: make the node a `SkinnedFigure`, `Show`
  its look once, call the matching `Pose…`. Static figures (a sleeper, the character page, a
  model viewer still, a vehicle's baked rider) keep the builders.
- A new garment or part: draw it under the right `RestMap` scope (it already is if it hangs off
  the rig like the others) and run `--skincheck`. Something drawn outside every scope rides on
  bone 0 and will not move.
- A new pose input (a new rig field the builder draws from) must reach `Frames` too.
