# Gadgets (#275, part 5 of the #270 epic)

- **Items** (`ItemId` 180-185, `ItemUse.Gadget`, workbench recipes in `Recipes.All`): zipline (3 rope + 2 scrap),
  rope ladder (2 rope + 2 planks), tyre trampoline (3 tyres + 2 rubber + 4 planks), launch pad (car battery +
  2 electronics + 4 scrap + cloth), camo net (4 cloth + 2 rope), hay bale hideout (6 firewood + 2 rope + 2 cloth).
  Icons are the generic glyph ones for now.
- **Placed objects** (`PlacedKind` 5-10, the `item-net-events` note): server-owned, saved, snapshot on join,
  only the owner takes one back (Aim + Use with the same item in hand, within 4 m; the item comes back).
  A kind's own rules: `Gadgets.Check`, called by `PlacedObjects.ServePlace` (and by the ghost).
  - Zipline: placed at its **low** post, payload = the high post "E;N;alt" (invariant). 8-150 m, at least 2 m
    downhill, at most 45°. Two Uses: the top post, then the bottom one (the ghost shows both posts and the cable).
  - Rope ladder: placed at its top, payload = length (1.5-8 m, measured by a ray down beside the edge aimed at).
  - Others: on flat ground (normal.Y >= 0.75), facing the player (local +Z toward them).
- **Looks and solids** (`GadgetMeshes`, through `A()` because MeshScratch turns meshes half round): posts with
  a cable, rope ladder with rungs, a tyre-rimmed mat on plank legs (cylinder collider, top at 0.46 m), a pad with
  chevrons and a light, a net of green/brown patches on four poles (only the poles are solid), a hollow bale
  hut 1.5 m high (crouch to be inside) with a door toward the placer and an eye slit behind.
- **Riding** (`GadgetTool`, local player only; others just see the body's replicated position):
  - E (`InteractMount`, from `ItemController` and from `FootPlayer.TryInteract`) by the high post (2 m), at a
    ladder's foot, or on a pad starts a ride through `FootPlayer.Carrier` with the new `ShowWhileCarried`.
  - Zipline: hands on the cable, feet 2.05 m below; gravity along the cable minus drag (`0.03 v²`), up to 16 m/s;
    Jump lets go, near the low post it sets you down (`FootPlayer.Release`, new: on foot, no ride change).
    Items still work while riding (shoot from a zipline).
  - Ladder: forward/back climb at 2.2 m/s (`ForceClimb` for probes); at the top it puts you on the ledge behind
    it, at the bottom on the ground, Jump pushes off.
  - Launch pad: 2.6 s up to 80 m (ease-out) drifting 10 m forward, then `Leap` into the wingsuit (Jump opens the
    parachute as on any base jump).
  - Trampoline: walking or landing onto the mat (no key) throws you up at 15.5 m/s (~13 m) and sets
    `FootPlayer.SoftLanding`: the next landing deals no fall damage.
- **Not yet**: a hanging / climbing pose for others (they see a standing body moving), hay bale burning from a
  flare, hiding from the minimap (no enemy markers exist yet), pixel icons, Battle Royale prefabs using them (#276).
- **Checks**: `--gadgetcheck --systems ui,physics,build` (offline: the rules, the real place path for trampoline /
  net / hideout, a bounce of 13 m without damage, the zipline's two-Use path refused when too short, a ride
  to the bottom post, a 5 m climb, an 80 m launch into the wingsuit, cleanup; `shots` windowed writes
  `test_output/gadget_*.png`); `tools/gadgetnetcheck.sh` (tier net: B joins after, gets both from the snapshot,
  sees A's body travel 30 m along the cable, sees both go).
