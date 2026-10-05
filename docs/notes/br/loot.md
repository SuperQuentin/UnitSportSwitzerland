# Battle Royale loot (#194, part 4 of #177)

- **Building loot** (`Loot/MatchLoot.cs`, `LootTables.MatchEpoch`):
  - While a match runs (Playing/Ended), `BrManager.SetMatchLoot` sets
    `LootTables.MatchEpoch = key -> epoch if the building's tile touches the region, else null`. It is
    set on the server and on every client (from the state).
  - `Epoch()` returns the match epoch (`1e9 + seed`), so every match restocks the region.
    `ContentsOf()` rolls `MatchLoot.Roll(MatchLoot.ForFurniture(type), RngFor(key, i, epoch))`
    instead of the free-roam table.
  - `LootService.MaskOf/SetMask` keep a match's taken marks in memory (`_matchMasks`), never in
    `user://loot/`. `ForgetMatch()` clears them at the end. Free-roam loot and its files are untouched.
  - Gun lockers and safes still need the dial. Their combination uses the match epoch.
- **`MatchLoot.Roll(MatchTable, rng)`** (pure). A gun always comes with rounds for it.

  | Table | Contents |
  |---|---|
  | Furniture | 50 % hold something: 1-2 rolls, 30 % weapon (pistol 38 > shotgun 26 > rifle 9 > knife 6 > hunting rifle 3; #455 made the rifle rarer), else supplies (9 mm, shells, 7.5 mm, bandages, first-aid kit, vest 8, food) |
  | GunLocker | a rifle or a hunting rifle + 7.5 mm, sometimes a vest |
  | Safe | a vest + a first-aid kit, sometimes a pistol |
  | Supply | 1-2 commons |
  | Military | a rifle, shotgun or hunting rifle + 7.5 mm, a bandage, half the time a vest |
  | Airdrop | a rifle or a hunting rifle + 30 rounds, a vest and a first-aid kit |

- **Crates** (`BattleRoyale/BrCrates.cs`, node `World/BrCrates`, server + clients). A `Crate` has an id, a
  `CrateStyle` (DeathBox, Supply, Military, Airdrop), LV95 E/N, `Alt` (`BrCrates.Ground` = on the ground
  wherever the client finds it), `LandsAt` (server clock), a label and the stacks.
  - **Server**: `Spawn(crates)` (one `AddMany` JSON broadcast), `ClearAll()`, `SendTo(peer)` on join.
  - **Taking**: `RequestTake(id, index, item, count)` is checked against all of these: the crate
    exists, it has landed, `MayLoot(peer)` (a living entrant, during the results too), the body is
    within about 5 m horizontally (and 4 m in height when the crate has one), and the stack still matches.
    - If it passes: `Changed` / `Removed` is broadcast **before** `Granted` to the taker, so "take
      all" goes on from the new contents. Empty crates are removed.
    - Otherwise: `Refused`, plus the current contents.
  - **Clients**: draw the meshes (`MeshScratch` + `ItemDefs.Material`): a red-lidded death box, a
    wooden supply crate, an olive army crate with the Swiss cross, and a blue airdrop with a striped
    canopy and red smoke (`CpuParticles3D`) while it falls.
    - Placement: snapped to `GroundAt` (the client's `ChunkManager.TryGetHeight`). A death box keeps
      its exact altitude, so one dropped indoors stays in the interior. An airdrop sits
      `min(270, left × 6 m/s)` up until `LandsAt`.
  - **E**: `FootPlayer.TryInteract` asks `BrCrates.TryOpen` first, indoors or out. The prompt comes from
    `InteriorManager.UpdatePrompt`.
  - The panel is the loot panel: `LootService.OpenCrate` (crate mode: `OpenContents`/`Take` go to
    `BrCrates`; `CrateGranted`/`CrateRefused`/`CrateChanged`). It closes when you walk away.
- **Spawning** (`BrLoot`, server, at GO, async):
  - **Road points**: `RoadPoints(source, area)` gives a point every 25 m along Major/Road/Minor/Lane/Track
    roads (no tunnels or bridges), with altitude and heading, from the server's own chunk source.
  - **Crates** (`RoadsideCrates`): 7 supply crates per km², 2.5-4.5 m off the road. Army depots:
    0.5 per km², three crates each, on tracks.
    - With fewer than 200 road points (a generated world), crates are scattered over the square on the
      ground instead.
  - **Vehicles** (`ParkVehicles`): 0.7 per km² on the verges, 30 % motorbikes, plus a helicopter at
    6 km and up. Placed through `VehicleManager.Place` and named `br_v*`; `RemoveVehicles` clears them
    at the end.
  - **Measured**: 5 km Grono has 30,732 road points, 211 crates and 17 vehicles. 6 km Roveredo has
    38,577 points, 252 supply and 54 army crates.
- **Airdrops**: max(2, km²/12) in total, spread over the start of phases 2, 4 and 6 (`DropsAt`). Each
  lands inside 70 % of the next circle and falls for 45 s. It is announced in chat with its grid
  square ("coming down in C4!") and drawn on the minimap, the full map (a blue box, plus its canopy while
  falling) and the compass (nearest within 2 km).
- **Death boxes**: the dying client reports its stacks with `ReportDeath(killer, cause, ids, counts)`
  (no `Data` stacks), then clears its pack. The server puts the box at the body's position with the
  label "<name>'s things". A disconnect leaves no box.
- **End**: `Finish` → `ClearLoot()`: crates gone, vehicles gone, `MatchEpoch = null`, `ForgetMatch()`.
- Not yet (part 4b): bunkers, hunter's high seats, hay-barn stashes, SAC boxes, the helicopter wreck,
  destructible crates, the flare gun. Also not yet: a server-side check of a death box's contents.
