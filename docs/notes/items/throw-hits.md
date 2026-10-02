# Thrown things hit people (#261)

- **Where it is tested** (`src/Items/ThrowHits.cs`): on the peer simulating the flight only (the
  thrower's `DroppedItem` proxy, then its authority body; a `RadioBody` before it settles), each
  physics step for 4 s: the segment travelled vs every other non-NPC, not-down player's vertical
  cylinder (r 0.45, 1.85 m), at 5 m/s or more. One bonk per throw (`_bonked`, carried over by
  `DroppedItem.TakeOver`). On a hit the item bounces back off the body (velocity reversed x0.25 + up).
- **Damage** `DamageFor(speed, mass)` = (speed - 3) x (0.55 + 0.45 mass), 3..22 (`MaxDamage`).
  **Never lethal**: the victim takes at most `Health - Floor` (5) (`FootPlayer.Bonked`).
- **Network**: `ItemEventKind.Bonk` (5, extra `victim|damage|item`). The server (`ItemEvents.RelayBonk`)
  checks damage <= MaxDamage, velocity <= 45 m/s, the victim within 8 m of the hit and the thrower
  within 60 m, then relays to the victim and to every peer that sees thrower or victim. Not gated by
  `PvpRules` (not a weapon, not lethal). `Deliver` keeps the hit position (no muzzle snap).
- **Reaction** (`ThrowHits.OnBonk`, every peer): `SfxSynth.BonkBank` (hollow falling knock),
  `OofBank` (a voiced grunt through two formant resonators, pitch per player), `DizzyBank` (a little
  square-wave "seeing stars" tune: four notes down and a trill); `FootPlayer.Flinch` rocks the figure
  about the hips away from the blow (0.42 rad x strength, damped spring, 1.2 s) on every copy. The
  victim's own machine: health, screen shake, a step back (2.2 m/s + up 1.6) and the dance stops. The
  thrower sees a hit marker.
