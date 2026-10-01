# Gun lockers and safes (#165)

- **What**: `FurnitureType.GunLocker` (0.6x0.45x1.8, shotgun 40 / shells 60, rolls 1-2, 15% empty) and `Safe`
  (0.6x0.6x0.85, gadgets/shells/medical/smart binoculars/shotgun + 90% 50-400 CHF). Shotguns and shells
  (category Gear) are in `Tiers` only so these pools can draw them; no category pool picks them up. Shells come
  as a box of 4-12. Always fully stocked (`AbundanceFor`).
- **Where** (`InteriorGenerator.Secure`, own seed `key|secure`, after furnishing so the rest of the plan does not
  depend on it): by kind, a chance and preferred rooms — house/other 40% locker (bedroom, storage, office,
  living, hall) + 15% safe; apartment 25% locker per floor + 10% safe; farm 55% locker; shop 75% safe (office,
  shop, store, lobby); works 50% safe; civic 30% safe + 20% locker; shed 12% locker. Random room of the first
  preferred type with space, at most one per room.
- **Mesh**: the body is an open-fronted steel box with guns/ammo or a cash box inside (`InteriorMeshBuilder`);
  the **door is its own node**, `InteriorMeshBuilder.LockDoor` in its hinge frame (left front edge), added by
  `InteriorNode.Create` (`Lock<i>/Hinge/Door`) and swung by `InteriorNode.SetLockOpen(i, open, animate)` (tween,
  -1.9 rad). An interior node is **freed** when you leave and rebuilt on the way back: its doors start shut, and
  never keep a reference to an old node (a disposed hinge throws).
- **Dial** (`LockPickUi`): 0-99, 3 tumblers (locker, ±1.6) or 4 (safe, ±1.0), directions alternate
  (clockwise raises the number under the mark). A/D, arrows, stick (`move_left/right`), mouse drag around the
  dial; Shift/Sprint slow. Faint tick per number, lower rasp within 7 of the number turning the right way, a
  loud click entering the window; the dial must then **rest** `SettleSeconds` (0.35) for the tumbler to drop —
  creeping on or overshooting loses the click. Movement is measured per frame from the dial value, so a probe
  calling `Turn` counts as movement too. The **wheel is not used** (it is the hotbar's).
- **Authority**: the combination is `LootTables.Combination(key, furniture, epoch, type)`, derived like the
  contents. The client computes it for the feedback, sends `RequestUnlock(key, furniture, epoch, combo)`; the
  server checks the player is in the building, the epoch is current and the numbers match, then sets
  `LootService.UnlockedBit` (1<<30) in that container's saved take mask (`user://loot`). A restock (new epoch)
  relocks it for free; the low bits still work as the take mask. `ServeContents`/`ServeTake` answer `Locked`
  for a locked container, so the contents cannot be fetched around the dial. Not anti-cheat: a modded client
  knows the combination (it is derivable); what the server guarantees is one shared opened state and loot.
- **Replication**: `Unlocked(key, i, epoch, mine)` goes to the cracker and every peer whose `SpaceOf` is that
  building (door swings, sound); entering a building with locks sends `RequestLocks(key)` -> `LockStates`
  (anyone may ask: it is only door state). The cracker's client then opens the loot panel itself.
- **Check**: `CHUNKS=<terrain_chunks> GODOT=<exe> tools/locksynccheck.sh [epoch] [E,N]` — server + clients A/B
  (`LockSyncProbe`): A's smart binoculars read the building at its door, B's wrong combination is refused
  (server logs `wrong combination`), A cracks the real dial, B sees the door open by itself and the same stacks,
  B walks out and back in and the server's state reopens it. Screenshots `test_output/locksync_*.png`. Same
  caveats as `lootsynccheck`: fresh epoch, writes the real `user://loot`. One run in five failed once on B's
  first E (dial did not open, cause not found); rerun.
