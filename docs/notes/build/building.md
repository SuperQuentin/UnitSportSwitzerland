# Building (#274, part 4 of the #270 epic)

- **Hammer** (`ItemId.Hammer` 150, `ItemUse.Build`; workbench: 2 scrap metal + 1 planks; in the Battle Royale
  starting kit with 15 planks). Held: a green/red ghost (`BuildTool.Step`) where the view points, with a line
  naming the piece, its cost or why not. **Use** builds, **Aim + Use** takes back your own piece (full refund),
  **Aim + wheel** picks the piece, **R** turns it, **Aim + R** changes material. R is the travel picker on
  foot: with the hammer in hand `ItemController` takes it first (deeper node, handled), so put the hammer away
  to travel. No pad bindings for turn/material yet.
- **Rules are plain C#** (`src/Build/BuildGrid.cs`, unit-tested in `BuildGridTests`): 2.4 m cells and storeys;
  `Slot` = cell + class (floor, edge, volume); an edge is stored on its cell's −Z (0) or −X (3) side only
  (`Slot.Edge` normalises). Pieces: floor, wall, window wall, door wall (open doorway, no leaf), half wall,
  stairs (45°, the foot limit is 52°), gable roof, pillar. Materials: wood (5 planks, 150 HP, 2 s, span 6),
  stone (8, 300, 4 s, 4), metal (4 scrap + 2 screws, 500, 6 s, 10), sandbags (3 gravel bags, 400, walls only,
  span 2). Half wall / pillar: half the cost (rounded up), 0.6× HP.
- **Support**: pieces touch at points of a half-cell lattice (`Touches`: a floor's edges and centre, a wall's
  foot, top and sides, stairs' low and high edges, a pillar's foot and top). A 0-1 shortest path from the
  grounded pieces: resting on something (a wall on a floor, a floor on a wall's top, stairs on a floor) costs 0,
  hanging off sideways costs 1; past its material's span a piece falls and holds nothing. A wall on the edge
  between two cells carries the floors on both sides. `Fallen` after a break = what collapses (debris).
- **Grounded** (client `BuildTool.Ground`, server `Structures.GroundOk` where it holds the terrain): the foot no
  more than a storey above the highest ground under it, not buried more than 1.2 m, legs at most 6 m
  (`StiltMax`). Grounded pieces get legs down to the terrain (`StructureVisuals.Legs`, retried while the
  terrain streams in, with colliders). A piece a storey or more up must rest on others.
- **Growth**: a piece starts at a tenth of its HP and grows to full over its material's time (`GrownHp`); the
  mesh grows up out of its foot over the same time. A fresh wall breaks easily.
- **Network** (`Structures` at `World/Structures`, the `PlacedObjects` model): server-owned; RPCs `AskBuild`,
  `AskRemove`, `AskHit` → `AddStructure`, `AddPiece`, `SetDamage`, `RemovePieces`, `RemoveStructure`, `Clear`
  + `Answer`; the whole list to a joining peer. Server checks: piece valid, ±48 cells, storeys −4..24, reach
  9 m from the body to the piece's centre, 400 pieces per owner (300 in a match), grounded plausible,
  `CannotPlace` (slot free, material allowed, stands). The builder pays first (`TakePlain`) and is refunded on a
  refusal. Free-roam structures saved to `user://structures/server.json` (offline `offline.json`), kinds and
  materials by name; match ones in memory, swept 2 s after `BrState.Running` ends.
- **Damage**: the shooter's own ray (`BuildTool.TryHit`, from `ItemController.Shoot` and the knife) meets a
  piece first → `SendHit` with `DamageAt × pellets × 0.8` (pellets mostly land that close). Server: capped at
  the weapon's `MaxHit` (shotgun 121.5), the owner may always, others only with `/pvp on`; in a match any
  living entrant (`BrManager.Playing`). Player shots already stop at any non-player collider, so cover works.
- **Switchable**: `Systems.Build` ("build"); null-safe users (`Structures.Instance?`).
- **Not yet**: ladders and gadgets (#275), Battle Royale prefabs and the match material economy (#276), road /
  property restrictions in free roam (only "not indoors"), interest by distance (everyone gets every structure),
  doors that open, pad bindings, building off a structure across another structure's grid.
- **Checks**: `tools/test.sh unit` (`BuildGridTests`); `--buildcheck --systems ui,physics,build` (offline:
  real aim/Use path, a hut of every piece and material, refusals, the span row, a capped hit ignored, the grown
  metal wall breaking on the 5th 120 hit and its 8 floors falling, a real shot trace, a refunded pick-up,
  cleanup; `shots` + windowed + `--view third` writes `test_output/build_*.png`); `tools/buildnetcheck.sh`
  (tier net: B joins after A built, snapshot + drawn, B refused damaging/taking A's pieces with PvP off, A's own
  wall breaks and its shared floor stays, everything gone at the end on both).
