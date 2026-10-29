# Sword combat (#1371, phase 1 of #1370)

Action-RPG melee on foot: a sword in the hand turns the combat controls on. Our own design. Spells, elements and monsters are
later phases (#1370) and plug into the same service and machine.

## Pieces

| What | Where |
|---|---|
| Frame data, the machine (`SwordMachine`), stamina, geometry (`InCone`, `Best`), the blade arc | `Combat/SwordRules.cs` (pure, unit-tested) |
| Server guard: rate, stamina, one hit per target per swing, i-frames | `Combat/SwordGuard.cs` (pure) |
| Whole-body keyframe tables, variants, jitter, lunge | `Combat/SwordPoses.cs` (pure); mapped on the dance channels in `Avatar/HumanMeshBuilder.Sword.cs` |
| `World/Sword`: moves, hits, dummies, bots' path | `Combat/SwordService.cs` |
| The local player's machine, lock-on, steps, trace, what a blow does to a body | `Player/FootPlayer.Sword.cs` |
| Training dummy, HUD, sounds, effects | `Combat/TrainingDummy.cs`, `SwordHud.cs`, `SwordSounds.cs`, `SwordFx.cs` |
| Bots | `Bots/BotHands` (`Swing`, `Dodge`, `SwordTick`), `BotIntent.SwordAt` / `DodgeFrom`, `/bot arm sword`, `/bot swing` |
| Check | `Combat/SwordNetProbe.cs`, `tools/swordnetcheck.sh`; poses: `tools/sword-sheet.py` |

## The moves

Item `ItemId.Sword` (600, the combat block is 600-619), `ItemUse.Sword`. `/spawn sword`. Times in seconds.

| Move | Input | Startup / active / recovery | Cancel | Reach m, cone | Damage | Push m/s | Hit-stop |
|---|---|---|---|---|---|---|---|
| Slash 1-4 | tap (again inside the cancel window) | 0.10-0.12 / 0.08-0.10 / 0.16-0.20 | 0.20-0.24 | 2.3-2.4, 55-60° | 12 / 13 / 15 / 16 | 3.5-5 | 0.05-0.06 |
| Slash 5 (finisher, a jump and a slam) | the fifth | 0.18 / 0.12 / 0.34 | 0.40 | 2.7, 75° | 24 | 9 | 0.10 (x1.6 on a kill), shake x1.7 |
| Charged | hold after a swing (0.45 s min, 1.2 s full), let go; 25 stamina | 0.15 / 0.12 / 0.45 | 0.50 | 3.0, 85° | 40 | 11 | 0.13 |
| Plunge | attack while falling (> 2.5 m/s down); dives at 22 m/s, lands soft | blade live on the ground | 0.30 | 2.8, all round | 30 | 9 | 0.10 |
| Dodge | the dodge key; 20 stamina | 0.38 total, untouchable 0.03-0.27 | 0.26 | - | - | dashes 9 m/s | - |

- A press up to 0.22 s before the cancel window is kept. After a swing, the next press within 0.35 s continues the combo; later it
  starts over. Dodge cancels a swing's recovery, never its startup.
- Stamina is `SwordStamina` (100, refill 30 /s after 0.7 s), not the Adventure ring: that one is infinite outside the Adventure layer
  and a fight needs it everywhere. Swings are free; the charge and the dodge pay. The HUD bar blinks when one was refused.
- **Soft lock-on**: a swing turns to the target nearest the view inside 55° and 5.5 m (`SwordRules.Best`). **Hard lock**: the lock key
  picks the target nearest the view within 16 m; the body faces it, the camera eases round, a target that goes behind or away lets go.
- **Feel**: hit-stop freezes the machine and the pose (`SwordMachine.Freeze`), screen shake (`FootPlayer.SwordShake`, scaled by the
  screen-shake setting), rumble, knockback (a push eased out, a stagger, a hard blow breaks the victim's swing), damage numbers
  (`SwordFx`), a spark and a light. A dummy leans away on a damped spring.

## Body animation

The whole figure is posed by its bones through the dance layer (`DanceParams.Move = SwordMoves`, the move, variant and seed packed in
`Bar`, `BarPhase` the progress through the move): chest twist and lean, hips, head, both arms (the sword hand's whip, the off hand's
counterweight), both feet. Each move is three phases between the stance: anticipation (eased in to the coil as the blade goes live),
strike (fast ease-out to the end of the blow), follow-through and settle (ease-out, then a small overshoot back). Tables are data in
`SwordPoses`; no `LookAt`, nothing rebuilt per frame, nothing allocated per frame.

- Five distinct swings: a diagonal right-to-left slash, a backhand rising slash, a horizontal spin slash (a 360° turn of the body,
  done on the owner and replicated as its rotation), a thrust with a lunge step, a jumping overhead slam. Charged: a crouched wind-up
  and a spinning release. Plunge: a tucked fall, a kneel on landing. Dodge: a roll forward, a backstep or a sidestep by the dash
  against where the body faces.
- **Two sets** of combo swings by context: standing (planted, shorter lunge) or on the move (a stick held or over 2 m/s: deeper lean,
  longer lunge). **Jitter**: the swing count's low four bits seed a few degrees of arc and a few per cent of wind-up timing,
  deterministic (`SwordPoses.Jitter`).
- **Lunge root motion**: 0.3-1.2 m per swing (`SwordPoses.Lunge`), eased out over the strike on the owner's body, stopped when a body
  is within 1.1-1.3 m ahead.
- **Replication**: the existing `Act` / `Acted` pair carries the move, the yaw and a 6-bit code (variant, seed): the peer that sees it
  draws the same variant and timing from its own clock. No per-frame stream. The weight blends in over 0.1 s and out over 0.25 s so
  walking does not snap.
- **Trail**: one fixed crescent mesh on the sword node, faded through the active frames only (`HeldItemVisual.StepTrail`), mirrored for
  the backhand, upright for the slam and the plunge.

## Network and authority

- Client -> server: `Act(move, yaw, code)` when a move starts (and `Charge` / `None` for the wind-up, so peers draw it), `Hit(move,
  kind, id)` for each target the owner's trace reached. Server -> peers that see the swinger: `Acted`; to everyone near:
  `Struck(attacker, move, kind, id, damage, hp, knock, at, down)`; dummies `DummyAdd` / `DummyGone` / `Healed`.
- The server takes a hit only when `SwordGuard` says the blade could be live (not before half the startup, once per target per swing,
  at most 6), the target is within the move's reach and cone of its own copies of the two bodies plus 1.6 m and 30° of slack, a
  dummy always, a body only when PvP allows it (`PvpRules.Allows`) and not inside a dodge's window. **The damage is the move's, never
  the client's.** Acts are refused when they come faster than the last move's cancel window allows or when the stamina the server
  counts (12 points of leeway) cannot pay. Refusals log `[sword] refused ...`. Wall clock (`RealClock`), like `HitGuard`, so
  `--checkspeed` is not used.
- The victim's own machine takes the health (`ShotHit`) and the push, as for guns; a bot is run on the server and takes both there.
- Not checked on the server: walls (no building colliders there), as in `pvp-weapons`.

## Controls (decided together)

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| swing, combo, charge (hold after a swing), plunge | LMB (`use_item`) | RB | R trigger (sends RB on foot) |
| dodge (`sword_dodge`; held on it, Shift carries on as the sprint) | Shift | B (the pad's crouch and slide wait while a sword is drawn) | B (`XrPad.RightB`) |
| lock on / let go (`sword_lock`; takes the swap-shoulder and view-switch buttons while a sword is drawn) | Middle button, Z | R3 | R3 |

Shown through `InputHints` (the sword's blurb, the prompt bar, F1 "Sword"). VR gap: the swing for real (hand speed), a wrist display for
the stamina bar (the HUD is off in VR).

## Dummies

`/dummy` puts one 2.6 m in front of you, `/dummy clear` takes yours away (4 each, 12 on a server). 300 HP, a readout (health, last blow,
total), down for 3 s at zero, healed after 6 s untouched. Solid on the clients; the server holds data only. `[Showcase("Combat")]`.

## Bots

`BotIntent.SwordAt` swings (a held intent chains the combo; the hands pulse the button and never charge), `DodgeFrom` dodges. Both run the
same `SwordMachine` on the server and go through `SwordService.ServerAct` / `ServerHit` / `ServerTrace`: the same guard. `BrBrain` swings
when it carries a sword and no gun and the target is in reach. Not done: a brain that dodges, charges or uses the plunge.

## Checks

- `tools/test.sh unit`: `SwordRulesTests` (combo, buffer, charge, plunge, dodge, stamina, hit-stop, cone, lock, guard), `SwordPosesTests`
  (phase order and timing windows, ease shapes, determinism of the jitter, two sets, lunge ranges, code round trip).
- `tools/swordnetcheck.sh` (net): A puts a dummy down; B sees it, A's copy swinging, the blows and the recoil; the charged blow; the dodge;
  forged dodges and a hit nobody swung refused; a bot hits the dummy; PvP on hurts and knocks B back, off does nothing. `SHOTS=1 STYLE=ps1`
  takes B's pictures.
- `--models,<dir> --modelsonly "Sword poses" --modelsyaw 90 --style cartoon|ps1`, then `tools/sword-sheet.py <dir> <png>`: a contact sheet of
  every move, both sets, from the side.

## Round 3: blade-contact hits, manga timing, grip, off hand

- **Hits are the blade touching** (`Combat/SwordSweep`, `Items/SwordBlade`): the blade is a 5 cm capsule from hilt to tip, sampled through the
  move's spline pose and the wrist (`SwordPoses.Wrist`) at 16 moments of the strike window, in character space; the trail is built from the
  same samples. The owner's client sweeps it every physics frame against each target's own shape (a body: capsule r 0.42, 1.85 m; the dummy: post,
  torso, head, arms) and names a target at the moment of contact, once a swing. The server sweeps the same capsule over the swing's window (100 ms
  either side of the hit's arrival, from the announced yaw plus the move's spin and the two bodies' positions) with 0.30 m of slack; a forged hit
  from 3 m is refused (`[sword] refused ... the Slash1 blade never reached ...`). Removed: the reach-and-cone test (`InCone` with `LagSlack` and
  `ConeSlackDeg`) on the client and the server, and the height check. Soft lock and the lunge bring a target in reach: the lunge is as long as it
  takes to bring the move's forward reach onto the target (at most 1.2 m), the body turns to it through the wind-up.
- **Manga timing** (frame data): wind-up 0.24-0.28 s (a moving hold: the coil is reached about 0.11 s before the blade goes live and drifts), the strike
  0.10 s (finisher 0.12), an overshoot snap, a zanshin hold (0.18 s, finisher 0.30 s), the settle; the cancel opens inside the hold.
- **Grip**: both hands are built closed when a sword is equipped (`FigureLook.Fist`, once, on equip); the sword node is placed by the palm-centre
  socket (`SwordPoses.GripSocket`), its angle is the neutral wrist cock plus the wrist's whip from `SwordPoses.Wrist` only.
- **Off hand**: derived from the sword arm (a quarter of its swing, the other way, lagging 0.10 s), the hikite on the thrust, to the handle for the
  finisher and the charged cut, out to the side in a roll or landing; always inside a comfort box.
- **Accents**: impact frame and 5 degree punch-in on a finisher's or full charge's hit (`SwordImpact`, setting "Impact frames", never in VR), a squash
  on the dummy, a glint on the blade at full charge, a wider brighter trail with an edge line on the finisher. `--sworddebug` draws the capsules.
