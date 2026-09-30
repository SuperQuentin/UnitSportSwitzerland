# Gathering

- **Gathering** (`src/Loot/Gathering.cs`, hold **G / pad X** on foot outdoors — pad X is only
  tuck/sprint when mounted): a bar fills (water 1.2 s, stone 1.6 s, tree 2.2 s), moving 0.9 m
  cancels. What is offered comes from real data, in priority order: **water** where the cover
  raster is `Water` ahead at about foot height (not from a bridge) or a `Watercourse`/`Bisse`
  segment of the `.road` tile is within width/2 + 1.6 m — streams are too narrow for the raster;
  **firewood** 2–4 from a `.trees` tree within 2.3 m; **stone** 1–3 (quarry 2–3, +gravel bags on
  loose ground) on Rock/Scree/Boulders/Quarry cover; **dead wood** 1–2 on wooded cover with no tree
  in reach. `ChunkManager.TryGetCover` reads the raster (only kept for fine-stride tiles, i.e. near
  a player). Trees and streams are fetched per tile through the cached source and trees bucketed
  into 10 m cells. **Local only**, like the inventory: each 8 m spot / tree gives a few harvests
  (stone 4, tree 2, water unlimited) and regrows after 20 min, per session. A tree within reach
  outranks rock underfoot. Check: `<godot> --path . -- --gathercheck[,out.png] [--at E,N]` finds a
  shore, a flat treeless rock patch and a tree near the spawn and harvests each (adds to the real
  inventory; steep scree slides the player off the spot, so the probe picks flat rock).
