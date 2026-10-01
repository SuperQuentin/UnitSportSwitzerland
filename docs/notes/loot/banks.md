# Banks: teller desk and vault (#213)

- **Which buildings**: the data has no banks. `BuildingFootprint.IsBank(fp)` = Commercial, at least
  60 m² and 6 m a side, and `StableHash(key|bank) % 5 == 0` (about 4 banks around Riddes). Pure
  function of the tile: the server's plan (`InteriorLayout.Type = BuildingType.Bank`, `IsBank`) and
  every client's `DoorSpot.Bank` agree without sending anything.
- **Sign**: `Interiors/BankSigns` (client) puts a blue plate with a gold "BANK" `Label3D` over each
  bank door, from `ChunkManager.TileFurnished`, parented to the tile node.
- **Plan**: ground floor `BankProgram`: banking hall at the street end, vault behind it on the
  same side (so it opens off the hall), office and WC on the other side (or in front of the hall when
  there is only one side). Upper floors are offices. `InteriorGenerator.Counter` guarantees a
  `TellerDesk` (shorter desks, then any ground-floor room). The vault holds up to six
  `VaultSafe`s and a crate of coin rolls.
- **Money moves only at the counter**: the inventory has no claim button any more. E at the teller
  desk (`LootService.NearestCounter`) opens `Items/BankCounterUi`: deposit all cash, withdraw
  20/50/100/500/all. `Bank.Withdraw` is new (RPC `RequestWithdraw`, no overdraft). The server asks
  `Bank.InBank` (`LootService.InBank`: the peer's interior space is a bank plan) before every
  deposit and withdrawal; a refused one answers 0 and the pocket is untouched. Offline: no check.
- **Vault safe**: a locked container (`LootTables.IsLocked`), 4-tumbler dial with a narrow window
  (0.8), then a **Simon panel** (`Loot/SimonUi`, `NeedsSimon`): four pads (1-4, arrows/d-pad,
  click), one more pad shown each round, a wrong pad starts again from round 1. The sequence is
  `LootTables.SimonSequence(layout, i, epoch)`, derived like the combination, and its length is
  `4 + value/450` (max 14) where value = `LootTables.Value(contents)` (cash, 25 CHF an item, 300 a
  shotgun or binoculars): a richer safe is a longer memory test. The client sends dial numbers and
  sequence in one `RequestUnlock`; the server compares with `Combination ++ SimonSequence`. A
  refusal while the panel is open (a restock began) resets the whole crack.
- **Check**: `CHUNKS=<terrain_chunks> GODOT=<exe> tools/bankcheck.sh [epoch] [E,N]` (`BankProbe`):
  sign present, street deposit refused, counter deposit/withdraw/no overdraft, B's wrong Simon refused
  by the server, A cracks dial + Simon (with one wrong pad), B sees the door swing and the same
  stacks, then A stands in a house cellar's shelter / music room. Screenshots `test_output/bank_*.png`.
  Writes the real `user://loot` and `user://bank/accounts.json` (accounts BankA/BankB).
