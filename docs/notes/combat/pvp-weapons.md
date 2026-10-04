# PvP foot weapons (#178, Battle Royale part 1 of #177)

- **Weapons** (`src/Items/Weapons.cs`): `WeaponDef` per item: ammo item, damage per pellet, pellets,
  spread, range, falloff (full damage up to `FalloffFrom`, down to `FarFactor` at `Range`), fire
  interval, aim FOV, sound pitch, head multiplier. `MaxHit` = every pellet in the head (the server cap).

  | Item | Ammo | Damage | Full to | Notes |
  |---|---|---|---|---|
  | Shotgun | Shells | 9 × 9 pellets | 14 m | pump cycle, far factor 0.3 |
  | Pistol | 9 mm | 24 | 30 m | every 0.28 s, bloom 1.2° |
  | Assault rifle | 7.5 mm | 22 | 80 m | every 0.16 s, bloom 2.5° |
  | Hunting rifle | 7.5 mm | 70 | 400 m | head ×1.8, far factor 0.55; scope at 9° FOV, drawn with the binocular overlay |
  | Knife | none | 34 | 2.2 m | `ItemUse.Melee` |

- **Balance** (#455): each gun owns a distance band — shotgun inside ~12 m, rifle in bursts to
  ~80 m, hunting rifle beyond, the pistol an honest all-rounder (~86 DPS). Before, the rifle (26
  every 0.16 s, full damage to 120 m, ~162 DPS) beat everything past 10 m. A hunting-rifle head shot
  (126) no longer kills a full-health player with a vest.
- **Spread bloom** (`WeaponDef.BloomDeg`/`BloomShots`, `ItemController.Bloom`): each shot adds
  `1/BloomShots` of heat, heat cools to 0 over `BloomRecover` (1 s) idle, the cone is `SpreadDeg +
  heat × BloomDeg`. The rifle held down reaches full bloom in ~2 s (+0.25 a shot, −0.16 between);
  taps every half second never bloom. Client-side only, like the spread itself; no crosshair shows it yet.
- **Medical items take time** (#455, `ItemController.MedicalSeconds`): a bandage (25 HP) heals 2.2 s
  into its use and a first-aid kit (75 HP, was 100) 5.6 s in; switching items first cancels it and
  keeps the item (`HeldItemVisual.PlayOneShot(peakAfterHold)`). It must be on the hotbar (a pack slot
  cannot be held, and used to heal at once).

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

  When it passes, the hit goes to the **victim only**, and `PvpRules.HitRelayed` is raised (for match statistics).
- **Victim** (`PlayerHits.OnHit`): `FootPlayer.ShotHit(damage, shooter)`. Inside a vehicle, the vehicle takes the hit.
- **Shooter**: `HitMarker` draws a cross at the screen centre, red for a head shot, with a tick sound.
- **No rate limit or line-of-sight check on the server yet.** The damage cap and the distance checks
  are the only guards; the client is trusted, the same as `CombatManager`.
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
