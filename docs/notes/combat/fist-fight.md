# Fist fights: Tekken / Mortal Kombat style 1v1 (#495)

Two players on foot fight a best-of-three; real health, armour and items are untouched, nobody dies.

## Flow

- **Challenge** (`FootPlayer.TryEngageFighter`, last E target before the dance): look at another player on
  foot within 3.5 m (`PointedFighter`: a 0.4 rad cone of the camera, nearest the centre, from
  `PlayerSnapshot`), press interact. Prompt bar: `Challenge to a fight` / `Accept <name>'s fight` /
  `Challenge sent...` (`FightManager.Prompt`). Chat: `/fight <player>`, `/fight accept [player]`,
  `/fight decline`, `/fight leave` (forfeit). A challenge to someone who already challenged you is an accept.
- **Waiting**: the challenged player gets a System chat line and a box on `FightHud` (name, how to answer,
  seconds left). It lapses after `FightRules.ChallengeSeconds` (30 s).
- **Refused** unless both are on foot, up (`Down == 0`), within 12 m (`ChallengeRange`), not already fighting.

## Match (server, `Combat/FightManager` at `World/Fight`, rules in pure `Combat/FightRules`)

- Arena: the midpoint of the two players and the line between them (`View.Centre`, `View.Yaw`, A to B).
  Each round both are set on marks 3 m apart (`StartGap`), the fighter's client places its own body
  (terrain surface under the mark). Steps stay on the line, within ±6 m (`HalfLength`), never closer than 0.8 m.
- `FightMatch`: 100 HP a round, 60 s, first to 2 rounds. Phases Intro (2.4 s: ROUND n, FIGHT!) → Live →
  RoundOver (3.2 s: K.O. / PERFECT / TIME / DRAW) → … → FinishHim (5 s, only after a K.O. in the deciding
  round: the loser dazed, the winner's down, down + kick is the Fatality) → Done (4 s, then `Ended`).
  A time-out goes to the healthier, a draw is a round for both (Tekken), two draws a drawn match.
- Forfeit: `/fight leave`, disconnecting (`ServerWorld.OnPeerDisconnected` → `PeerLeft`), getting into
  anything, or being 18 m from the centre (a teleport).
- RPCs: client → server `RequestChallenge`, `RequestAccept`, `Strike(fightId, move)`, `RequestLeave`;
  server → the two fighters `Challenged`, `ChallengeOver`, `Begin`, `State`, `Struck`, `Ended`.

## Strikes: who decides what

- The attacker's client starts a move from `FightInput` (pure) and, when its active frames begin with the
  opponent within `Reach + BodyRadius` as drawn there, sends `Strike`.
- The server takes it only if: the round is live (the finisher: FinishHim and the winner only); the last
  move from that attacker has ended (`Total - RateLeeway`; a string kick may cut into a jab after its
  startup); its own copies of the two bodies are within `Reach + 0.42 + LagSlack (1.0)` m. Then
  `FightRules.Resolve(move, victim's replicated FightPose)`: highs whiff over a crouch, lows under a jump, a
  standing guard stops all but lows, a crouching guard all but overheads, nothing lands on a body that is down.
  Refusals log `[fight] #id refused ...`.
- `Struck` goes to both fighters: the victim plays the stun (`HitStun` / `BlockStun`), the push (half on a
  block) and a knockdown (lying flat via `PublishFootPose`'s down rotation), the flinch, shake, rumble and the
  impact sound; the attacker gets a rumble and the sound.

| Move | Input | Startup / active / recovery s | Reach m | Dmg | Level | Notes |
|---|---|---|---|---|---|---|
| Jab | punch | 0.10 / 0.06 / 0.16 | 1.15 | 5 | high | chains into a jab or the string kick after its active frames |
| Kick | kick | 0.20 / 0.08 / 0.28 | 1.45 | 9 | mid | |
| Low jab | crouch + punch | 0.12 / 0.06 / 0.18 | 1.10 | 4 | low | |
| Sweep | crouch + kick | 0.24 / 0.10 / 0.38 | 1.50 | 8 | low | knockdown |
| Jump kick | in the air + kick | 0.14 / 0.14 / 0.20 | 1.40 | 10 | overhead | |
| Uppercut | down, forward + punch (0.45 s window) | 0.16 / 0.10 / 0.45 | 1.20 | 14 | mid | knockdown |
| String kick | punch, punch, kick | 0.14 / 0.08 / 0.30 | 1.40 | 10 | mid | |
| Finisher | down, down + kick, FinishHim only | 0.30 / 0.20 / 0.60 | 1.80 | – | – | Fatality, launches the loser |

## Controls (fight context; items, wheels, emotes and E are off: `UsablePlayer`, `CanEmote`, `TryInteract`)

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| challenge / accept | E (look at them) | Y | Y, or reach out and grip (as every E target) |
| step | A / D, screen-relative (the camera is side-on) | L stick ←/→ | L stick ↑ towards / ↓ away |
| jump | W, Space | L stick ↑, A | A |
| crouch | S, Ctrl / C | L stick ↓ | crouch for real (`XrPad.RealCrouch`), or L stick ↓ |
| punch `fight_punch` | LMB, J | X, RB | R trigger (RB) |
| kick `fight_kick` | RMB, K | Y, LB | L trigger (LB) |
| block `fight_block` (hold) | Shift, L | B | B (`XrPad.RightB`; the real crouch's B does not guard) |

Pad B is also `crouch_slide`: in a fight the pad crouches on the stick only, so B is only the guard.
Prompt bar while fighting: Punch / Kick / Block; F1 has a "Fist fight" group; the HUD lists the specials
through `InputHints.Format`.

## Drawing

- `FootPlayer.FightPose` (`[Export] int`, OnChange, ≤ 0.1 s late): a `FightStance` or `MoveBase` + move.
  Every peer draws it with the dance layer (`StepFightPose` → `DanceParams.Move = HumanMeshBuilder.FightMoves`
  (3000) + pose, `BarPhase` = progress through the move from when that copy saw it start, a 1.2 s loop for a
  stance, 80 ms crossfade). Poses in `Avatar/HumanMeshBuilder.Fight.cs`: orthodox stance (the figure's left
  side leads, `Psi` −0.38), no groove (`DanceMove.FightStand` and after). A fighter's `PoseKind` is always
  Stride, so the dance layer draws it in the air too.
- Camera (`FightView`, not in VR): side-on from the arena's perpendicular, the local fighter on the left,
  side chosen once per fight, distance 3.6-8 m with the gap, `ArmReach` pull-in. VR keeps the head's view and
  never turns the body under the headset.
- `FightHud` (layer 8): bars draining to the middle with a white trail, round pips, the clock (red under
  10 s), banners. Text rebuilt only on change.

## Not done / gaps

- Spectators see the poses but hear nothing and have no HUD; only the two fighters get `State`/`Struck`.
- VR fists: punching for real (hand speed) is the R4 target; today the triggers punch and kick.
- A held item stays drawn in the hand (items are off, not holstered).

## Check

- `tools/test.sh unit`: `FightRulesTests` (guards, inputs, rate budget, rounds, time-out, draw, finisher, forfeit).
- `tools/fightnetcheck.sh`: server + A + B on `--world fixture`; A challenges B with `/fight`, B accepts,
  A lands jabs until the round ends, B sees A's punch pose on its copy, forged strikes from 20 m are refused.
