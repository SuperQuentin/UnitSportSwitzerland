# Figure body (#394): builds, lofted trunk and head, hands, boots, hair

- **One body for every figure**: `HumanMeshBuilder.AppendRig` (`HumanMeshBuilder.Clothing.cs`)
  draws walker, rider, driver, passenger, swimmer and ragdoll through `AppendBody`
  (`HumanMeshBuilder.Body.cs`). The skeleton (`Rig`, bone lengths, gait, IK, dances, seats) is
  unchanged and shared by every build; only the flesh round the bones differs.
- **Builds** (`BodyBuild`, replicated, append only): Slim, Curvy, Broad, Lean, Stocky. `Physique.Of`
  holds each build's half widths and radii at the landmarks (crotch, hip, waist, under-bust,
  chest, shoulder line, neck; thigh, knee, calf, ankle; deltoid, upper arm, elbow, forearm,
  wrist), hand and head scale, `Jaw` 0 soft / 1 square (the masculine builds).
- **Trunk** (`Torso`): 10-sided rings along the spine, parameter `s` 0 crotch, 1 hip, 2 waist,
  3 chest, 4 neck; each ring has its own front and back depth. `Torso.Band(s0, s1, colour, inflate)`
  lofts a stretch of it; `Surface(s, angle, proud)` / `Front(s, proud)` give a point on its flat
  facets, which is where the clothes lay prints, buckles, chains (`clothing`).
- **Limbs**: tapered tubes with a calf and forearm swell, coloured by `Zones` along the limb
  parameter (arm: shoulder 0, elbow 1, wrist 2; leg: hip 0, knee 1, ankle 2). `LimbBand` lays a
  band over a stretch of a limb (cuffs, sock tops, buckles), following its taper.
- **Hand**: palm box, four finger boxes curled toward the palm, a thumb tube; fingerless gloves
  colour the palm only. **Boot**: a 5-station loft along ankle→toe whose bottom bands are the
  sole's colour; `Platform` grows the sole up into the boot (the ground stays put).
- **Head** (`Head`): 12-sided rings up the head's axis, a wide cranium over a narrower jaw with a
  flat underside that overhangs a straight neck tube (no head-and-neck cone). About 6.5 heads
  tall. `Head.Top(hair)`, `HalfWidth(hair)`, `Point(y, angle, lift)`, `Ear(side)` place what goes
  on it (hats, glasses, masks, piercings).
- **Face**: `procedural-faces`. **Hair** (`HairStyle`, replicated, append only): a slanted cap (high on
  the brow, low at the nape, so short hair is not a beanie), a fringe (locks, spiky, blunt cut,
  swept), falls (`Skirt` open over the face), spikes, a crest, tails, a bun. `HairCover.Hat`
  (helmet, beanie, witch or santa hat) drops what would poke through; `HairCover.Head` (pumpkin,
  full-face helmet) draws no hair and no face.
- **No allocation per rebuild** (`perf-no-per-frame-allocations`): `BodyLook` is a record
  struct, `Torso`/`Head`/`Fit` structs, loft rings come from a per-thread `RingPool` (a loft
  reads its rings only while drawn). About 1.7-2.8 k triangles in the lit styles.
- **Lit styles**: lofted bands get round normals (out of each ring's middle) so the cel light
  rolls round the trunk. No ink outline round figures in Cartoon (dropped in #671).
- **Limbs are one skin each** (#671): `LimbLoft` lofts an arm from the deltoid's dome to the wrist,
  a leg from inside the pelvis to the ankle, through `(t, radius)` stations plus a cut at each
  colour change; the elbow's or knee's ring sits on the bisector, stretched across the bend like
  a mitre, and the rings are carried along without twist. One loft per run of one colour,
  neighbours sharing their ring, so no seam opens at a joint.
- **Head** (#671): round, not an egg: full jaw, short blunt chin, a domed crown (ring at 0.238);
  the hair cap has a ring there too, or the scalp shows through.
- Preview: `<godot> --path . -- --avatars <s> <png> --bodies builds|crowd|looks|guys|faces|eyes|hair|hair2|heads|walk [--style ps1|cartoon|real-] [--view deg]`.
  Check: `--outfitcheck` builds every build × hair plain, dressed, under a hat and a helmet.
- Hats sit on `Head.Seat(hair)`, just above the brow (0.184, 0.192 over hair), not on the crown:
  the head is round, so a band rests where it is as wide as the band. `Head.HatScale` scales the
  hats' radii (authored for a ~0.11 m band). The cycling helmet is a shell from the same seat.
  Check with `--avatars 2 <png> --hats --close` (every hat, then the helmet, heads only).
