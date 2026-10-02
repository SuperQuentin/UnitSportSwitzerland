# Battle Royale outdoor sites (#198, part 4b of #177)

- **Placement** (`BrSites.Place(source, area, seed, roads)`, server, after the roadside crates).
  - Data, per tile of the square: the 100 m horizon lattice (`BrMapImage.Height`, slope ±30 m),
    land cover, buildings, roads/paths/rivers.
  - The search itself runs on a pool thread, in about 0.4-1 s for 6 km.
  - Seeded by the match; candidates are drawn with a minimum spacing (`Take`).

  | Site | Rule | Count | Table |
  |---|---|---|---|
  | Army bunker (`Bunker`, **locked**) | 60 m grid, slope ≥ 0.6 (31°), within ~150 m of a road, not water/glacier; door faces downhill | 1 (2 from 6 km), 1.5 km apart | hunting rifle, vest, first-aid kit, half the time a rifle |
  | High seat | a forest cell (20 m grid) with open ground 25 and 45 m to one side; faces the field | 1.2 / km², 300 m apart | hunting rifle 45 % else shotgun; binoculars 50 % |
  | Hay stash | an agricultural building with at most one other building in its 3×3 neighbourhood of 80 m cells; beside its plan, at its base altitude (`MinY`); 35 % also get a motorbike (`BrLoot.PlaceBike`, `br_vbarn*`) | 1 / km², 250 m apart | bandages, food, sometimes knife/pistol |
  | SAC box | path points (Path class, or hiking-flagged tracks) above 1,500 m | up to 3, 800 m apart | first-aid kit, bandages, flare gun 35 % |
  | Helicopter wreck | open ground, slope < 0.25, inside 60 % of the first circle | 1 | rifle + 20 rounds, vest, flare gun 50 % |
  | Fishing hut | 8 m from a watercourse on land, or a land cell next to lake water; faces the water | 0.4 / km² (max 8), 500 m apart | ammo, canned food, sometimes a shotgun |

- **Measured**: Roveredo (GR) gave 2 bunkers, 43 high seats, 19 hay stashes (8 bikes), 3 SAC boxes,
  1 wreck and 8 fishing huts. Locarno gave no SAC boxes (low and lakeside), as expected.
- **Look** (`BrSiteMeshes`, built from `MeshScratch` boxes and tubes):
  - Bunker: a concrete face with camouflage and a steel door + dial; once cracked, a dark doorway with the door swung open.
  - High seat: legs, cabin, ladder, a box at its foot.
  - Hay stash: hay bales and a crate.
  - SAC box: a red box with a white cross on a post.
  - Wreck: burnt fuselage, tail boom, bent blades, scorched ground, two crates, grey smoke.
  - Fishing hut: a shed with a rod and a crate.
  - Pile: what a supply crate becomes when broken.

  **Orientation**: meshes are authored with +Z to the open side; `MeshScratch.Build()` turns them half round, so the node's
  −Z faces it. `Crate.Yaw = BrSites.YawFacing(dir) = atan2(−east, north)`. NaN means any way round.
  `Crate` JSON allows NaN (`AllowNamedFloatingPointLiterals`).
- **Locked crates** (the bunker door): `Crate.Locked`. E opens the lock-picking dial in crate mode
  (`LootService.PickCrate`). The combination is `BrCrates.Combination(id, seed)`
  (`LootTables.Combination($"crate{id}", 0, seed, Safe)`), computed on both sides and never sent.
  - `SubmitCombination` → `RequestUnlock`. The server checks the numbers, reach and `MayLoot`, then
    broadcasts `Unlocked(id, by)`: the door redraws open, and the cracker goes straight into the contents.
- **Breaking** a supply crate: `ItemController.Shoot` (after the player trace) and a knife stab that
  hits no player call `BrCrates.TryBreak(eye, dir, range)`, a ray against a 0.45 m sphere at each supply crate.
  - It sends `RequestBreak`. The server accepts a living entrant within 320 m, turns the crate into a
    `Pile` labelled "the broken crate", and broadcasts `Broken` (plank burst + impact sound). The
    contents stay, to be looted from the pile.
- **Flare gun** (`ItemId.FlareGun` 60, `ItemUse.Signal`):
  - Use → `BrManager.CallDrop()` → server `RequestDrop`. It requires a living entrant in Playing, at
    most once per 20 s per player. An airdrop lands 20-50 m from the body after 45 s, and chat says
    "X fired a flare: a supply drop is coming down in C4!".
  - The flare itself is `ItemEventKind.Flare` (4): a red light climbing 140 m, seen by everyone near.
  - Outside a match it does nothing, and the flare is kept.
- **Map**: the wreck always shows as an orange "?" on the minimap and full map (its smoke gives it
  away); a bunker shows once you are within 400 m (`BrMapDraw.Overlays`).
- **`CachingChunkSource.LoadHorizonAsync`** is memoised (cleared by `Invalidate`). The map and the
  site search asked for the whole-country horizon every match.
- Not yet:
  - climbing the high seat (the loot is at its foot);
  - a server-side check that a shot could see the crate;
  - bunkers inside the rock (the face is a facade);
  - a key → motorbike link at hay stashes (the motorbike simply stands there).
