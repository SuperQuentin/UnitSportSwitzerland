# Medic armband (#218): opt out of PvP

## Rule

- **One check, on the server relay**: `ItemEvents.RelayHit` drops every foot-weapon hit (guns, knife) where the
  shooter or the victim wears the armband (`Combat.Medic.Hurts` → `MedicLedger.Hurts`). The hit still counts as an
  attack (the attacker's cooldown restarts).
- **Damage applied on the victim's machine** goes through the same `Medic.Hurts(attacker, victim)`: aircraft rounds
  (`CombatManager.Hit`, the shooter's body found by authority). A new player-caused damage source asks it too.
  World damage (falls, crashes, birds, zone, traffic) never asks: medics take it. Throw bonks (`RelayBonk`, never
  lethal) are left alone. No damage path "ram by another player" exists yet; one that is added must ask.
- **Shooter side** (`PlayerHits.Targets`): a medic's guns hit only the world, and a medic is not a target: no Hit is
  sent and no hit marker drawn. The server check stays for a modified or stale client.
- **State**: `FootPlayer.Medic` is bit 60 of the replicated `OutfitBits` (`Outfit.MedicBit`, above the ten
  6-bit slots; `Outfit.IsEmpty` ignores it). So it replicates on change with the clothes, and every figure that
  draws an outfit (walker, riders, drivers, passengers, ragdoll) draws the armband: `HumanMeshBuilder.AppendArmband`,
  a white band with a red cross round the left upper arm, inside the figure mesh (no extra node or material, rebuilt
  only when the pose key or the outfit changes). `OccasionHats` keeps the bit when it republishes the outfit.
- **The server decides**: `/medic on|off` (the pause menu sends the same line). The owner sets the bit only on the
  server's `FootPlayer.MedicState` RPC (on, cooldown left, delay left; also what the pause menu's timer reads).
  - **Cooldown**: `MedicLedger`, keyed by player name (case-insensitive), so a rejoin keeps it. A relayed hit on a
    player restarts it; being attacked does not. `--medic-cooldown s` (300).
  - **Delay**: `--medic-delay s` (10) standing still (within 1.5 m), cancelled by a relayed player hit taken, by an
    attack made (the cooldown), by moving, or by `/medic off`. Taking it off is immediate.
  - **Battle Royale**: `Medic.InMatch` (set by `BrManager`) refuses it while a match the peer is in runs;
    GO takes entrants' armbands off (`Medic.Suspend`), `Finish` gives them back (`Medic.Restore`). The pause menu
    hides the entry in a match.

## Not covered yet

- Aircraft rounds and anything else applied on the victim's machine do not restart the attacker's cooldown (the
  server never sees those hits).
- No "medic" marker for the shooter; no cross on the name tag.

## How to check

- `tools/test.sh unit` (`MedicRulesTests`: who hurts whom, cooldown by name, BR refusal, delay cancel).
- `GODOT=<exe> tools/mediccheck.sh` (two clients, PvP on, BR included; screenshot `test_output/pvp_medic_a_sees_b.png`).
