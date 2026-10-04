# PvP foot weapons (#178, Battle Royale part 1 of #177)

- **Weapons** (`src/Items/Weapons.cs`): `WeaponDef` per item: ammo item, damage per pellet, pellets,
  spread, range, falloff (full damage up to `FalloffFrom`, down to `FarFactor` at `Range`), fire
  interval, aim FOV, sound pitch, head multiplier. `MaxHit` = every pellet in the head (the server cap).

  | Item | Ammo | Damage | Notes |
  |---|---|---|---|
  | Shotgun | Shells | 9 × 9 pellets | pump cycle |
  | Pistol | 9 mm | 20 | |
  | Assault rifle | 7.5 mm | 26 | every 0.16 s |
  | Hunting rifle | 7.5 mm | 70 | scope at 9° FOV, drawn with the binocular overlay |
  | Knife | none | 34 | `ItemUse.Melee`, 2.2 m reach |

- **Items** (appended to `ItemId`): `Pistol` 53, `Rifle` 54, `HuntingRifle` 55, `Knife` 56,
  `Ammo9mm` 57, `Ammo75` 58, `ArmorVest` 59 (`ItemUse.Armor`). Bandage and first-aid kit already
  existed. They are not in free-roam loot yet: get them with `/spawn`.
- **Firing** (`ItemController.UseSlot`, `Shoot`, `AimFrom`): `AimFrom` gives the eye-origin aim (the
  third-person camera ray), which used to live in `BirdLife.Fire`. The Shot item event carries the
  weapon id in `Extra` (empty = shotgun), which sets the sound's pitch and whether the pump runs.
  `ItemController.Fire(player, eye, aim)` is now called for the shotgun only (the bird hunt).
- **Hits** (`src/Items/PlayerHits.cs`): the shooter traces each pellet against the other real
  players' bodies as it draws them. A body is a vertical cylinder, r 0.42 m, 1.85 m tall from the
  feet; a hit at 1.5 m or higher is a head shot. Hits are blocked by any non-player collider. Skipped:
  NPCs, `Down != 0`.
  - One `ItemEventKind.Hit` (3) is sent per victim. Its Extra is `victim|damage|weapon|head`.
- **Server** (`ItemEvents.RelayHit`): it drops the hit unless all of these hold:
  - PvP is on (`Combat.PvpRules.Enabled`).
  - The weapon exists and the damage is at most `MaxHit`.
  - Neither body is down.
  - The two bodies are within `Range + 8 m` of each other, and the victim is within 8 m of the hit point.
  - The victim is connected.
  - (#468, `Combat/HitGuard`) **No hill in the way**: `TerrainClear` samples `InterestService.Ground`
    (the 100 m horizon lattice) every 10 m from the shooter's eye to the hit; blocked only where the
    ground is 15 m above the line plus each end's own depth under the lattice surface, blended along
    the shot (the lattice rounds ridges off and fills valleys in; rooms and tunnels lie under it). Not
    for the knife. Buildings are not on the server: shooting through a wall is still trusted.
  - (#468) **A real rate of fire**: `TryShot`, a token bucket per shooter and weapon, 3 shots deep,
    refilled at 1/(0.85 × `Interval`) a second. Hits within 50 ms of a shot's first are that shot's:
    up to `Pellets` different victims for free; the same victim again is another shot. An honest
    rifle never trips it, jitter bunching three shots passes, and a 100-shot/s cheat gets ~7/s.
    `tools/pvpcheck.sh`: 30 forged pistol hits in one frame, 3-5 arrive, none with PvP off.

  When it passes, the hit goes to the **victim only**, and `PvpRules.HitRelayed` is raised (for match statistics).
- **Victim** (`PlayerHits.OnHit`): `FootPlayer.ShotHit(damage, shooter)`. Inside a vehicle, the vehicle takes the hit.
- **Shooter**: `HitMarker` draws a cross at the screen centre, red for a head shot, with a tick sound.
- Refusals log `[pvp] refused peer A on peer B: …`. Still trusted: walls (no building colliders on the
  server), the aim itself, and aerial rounds (`CombatManager`, applied by the victim's client).
- **PvP switch**: `/pvp on|off` (admin; bare `/pvp` reports it) or `--pvp` on the server command
  line. Off by default, so free roam stays peaceful.
- **Player** (`src/Player/FootPlayer.cs`):
  - `TakeDamage(amount, attacker, DamageCause)`. The causes are Weapon, Blast, Fall, Crash, Zone and Other.
  - The attacker is credited for `CreditSeconds` (10 s), so a fall after being shot still counts as their kill.
  - `Died(killer, cause)` fires on 0 health.
  - `Armor` (max 50) soaks half of every *weapon* hit. `AddArmor` puts it on; it is not replicated.
  - `[Export] int Down` is replicated on change and is 1 while knocked out. The remote figure already
    lies flat (`BodyPose` from `_stunTimer`).
  - `static Func<FootPlayer,bool>? StayDown`: when it returns true, `Die()` sets `Eliminated` instead
    of the 3.5 s revive. `Respawn(at)` brings an eliminated player back.
- `Inventory.Clear()` empties the slots, cursor and bin; cash stays.
- Check: `GODOT=<exe> tools/pvpcheck.sh` runs two loopback runs:
  - **PvP on**: rifle, pistol through the vest, knife, the kill credited to A, Down seen by A, a downed body not hit again.
  - **PvP off**: no damage.

  Screenshots are written to `test_output/pvp_{a_down,b_down}.png`.
