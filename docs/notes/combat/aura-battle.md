# Aura battles: plan a combo, cut it on the beat (#907)

The 2026 street trend (two people face off, take short turns of poses, stares, dances and meme
moves, the crowd decides) as a 1v1 mode next to the fist fight. Nobody touches anyone and nothing
is at stake but the aura. Plan: `docs/plans/aura-battle.md`.

## Flow

- **Challenge**: `/aura <player>` or Activities → Invites "Challenge to an aura battle": an invite of
  kind `aura` (#840, `net/invites`), `Mutual`. Accept: `/aura accept [player]`, the toast, F2.
  `/aura decline`, `/aura leave` (forfeit). Both on foot, up, within 12 m; nobody in a fist fight
  (`FightManager.InMatch` / `AuraManager.InMatch` refuse each other).
- **Marks**: both stand `AuraRules.StartGap` (4 m) apart on the line through their midpoint,
  facing each other, walk locked (`FootPlayer.AuraPhysics`). Leaving 18 m, getting into anything or
  disconnecting forfeits.
- **Three rounds**, one turn each: A opens round 1, B round 2; round 3's leader opens so the one
  behind answers (`AuraMatch.PerformerOf`).
- **A turn**: Intro 3 s → **Plan** 15 s (the performer's planner; the rival sees "X is planning",
  the plan stays secret) → **Countdown** to the first bar at least 2 s on (`AuraRules.NextBar`) →
  **Perform** → **Verdict** 4 s (the parts counted up) → next turn. **Done** 6 s: winner and each
  battler's best combo (highest turn), also a chat line.
- **Perform, one key**: at 120 BPM on `ClockSync.ServerNow`, each move lasts its beats (2 or 4). The
  performer presses `aura_next` to cut to the next move; the press should land on the move's last
  downbeat (`AuraRules.CutAt`). The schedule is fixed by the plan: a press only grades the cut.
  - Perfect ±0.05 s (×1), Good ±0.10 (×0.8), Ok ±0.16 (×0.5), beyond: Miss (×0): early = rushed,
    late = frozen. Difficulty-3 moves narrow every window to ×0.85.
  - More than a beat before the downbeat is ignored (a double tap); no press by 0.25 s after it:
    the server calls it frozen (`AuraMatch.Tick`), a Miss.
  - A missed cut: a stumble over the next move's first beat (drawn), −2, and no chain through it.

## Moves (`AuraRules`, append-only ids, on the wire)

| Move | Family | Beats | Base | Diff | Tags | Answers |
|---|---|---|---|---|---|---|
| Stand tall | Pose | 2 | 5 | 1 | Calm, Finisher | |
| Arms crossed | Pose | 2 | 6 | 1 | Calm, Finisher | |
| Point to sky | Pose | 2 | 9 | 2 | Hype, Finisher | |
| Stare down | Stare | 4 | 8 | 1 | Calm, Opener | |
| Sunglasses | Stare | 2 | 9 | 2 | Cool | Stare down, Point to sky |
| Yawn | Stare | 2 | 8 | 1 | Calm | Siuuu, Griddy |
| Slow walk | Walk | 4 | 10 | 2 | Opener | |
| Griddy | Dance | 4 | 11 | 2 | Hype | |
| Siuuu | Meme | 4 | 14 | 3 | Hype, Finisher | |
| Mewing | Meme | 2 | 10 | 2 | Cool | Stare down, Sunglasses |
| Six seven | Meme | 2 | 10 | 2 | Hype | |
| Lasso | Mime | 4 | 13 | 3 | Attack, Opener | |
| Cut the rope | Mime | 2 | 11 | 2 | Surprise | Lasso |

A plan holds 3-6 moves, at most 16 beats; a short one (time ran out) is padded with Stand tall.

## Score (`AuraRules.Score`, `AuraScore` part by part)

- **Base**: Σ base × timing; a move's second copy in the combo ×0.5, third ×0 and −4; used in your
  previous turn ×0.5, an earlier one ×0.7; copied from the rival's last combo ("biting") ×0.5.
- **Variety** +3 per family landed beyond the first (cap 12). **Chain** between two landed moves:
  Walk→Pose +4, Walk→Stare +3, Stare→Meme +3, Dance→Pose +3, Mime→Pose +3, Stare→Pose +2,
  Meme→Pose +2, Stare→Walk +2. **Opener** +2 (first move, landed). **Finisher** +5 (last move, Good or
  better); a missed last cut −6.
- **Answer** +8 per rival move of their last combo countered by a landed move, ×1.25 when it is
  your first move; at most two, cap 18.
- **Streak** +1 per Perfect past the third in a row (cap 8). **Try-hard** −3 per difficulty point
  over 10, −5 per meme past the second. **Stumble** −2 per miss.
- **Crowd** +2 per spectator within 15 m who played Cheer, Clap or Fist pump during the turn (the
  emote wheel's replicated `DanceId`, scanned at 5 Hz), capped at +8 and a quarter of the rest.
- Total `max(0, round)`, shown ×100 ("+4100 AURA"). Worked examples in `AuraRulesTests`: the
  opening (stare down, slow walk, Siuuu, all Good) 41; answering it (yawn, sunglasses, point to
  sky, all Perfect) 54.
- `AuraRules.SuggestPlan(ctx)`: greedy best estimate (every cut Good): the planner's Suggest, bots.

## Net and authority

- `World/Aura` (`AuraManager`) on the server and every client. Client → server (`AnyPeer`,
  reliable): `RequestChallenge`, `SubmitPlan(id, turn, byte[])`, `Next(id, turn, index, ts)`,
  `RequestLeave`; each calls a public server method (`SubmitPlanFor`, `NextFor`) a bot can call.
- A plan is checked (`AuraRules.Accept`) and only then sent to both with its first downbeat
  (`Perform`). Server → the two: `Begin`, `State` (on change), `Perform`, `Graded`, `Verdict` (moves,
  grades, the score's parts), `Ended`.
- **Press time**: the client stamps its `ClockSync.ServerNow`; the server keeps it while it agrees
  within 0.12 s with arrival − rtt/2 (ENet's round trip), else uses that; a stamp 0.05 s past its
  arrival is refused. Limitation: a modified client can still fake a perfect stamp inside that slack,
  the usual limit of networked rhythm games.
- Replicated: only `FootPlayer.AuraPose` (OnChange, owner-written; in `ActorIo` for movie clips).
  Nothing is scored from it.

## Drawing

- `HumanMeshBuilder.Aura.cs`: `AuraMoves = 5000` + `AuraPose`; `DanceMove.AuraWaiting..AuraCutTheRope`
  sit past `FightStand` (no groove). A move's `BarPhase` is its progress over its beats from when
  this copy saw it start; the Griddy is the dance itself on the shared beat. Stances: waiting (arms
  crossed), planning (hand on chin), stumble, victory, defeat. `[Showcase("Aura")]`; `--emotecheck`
  builds every pose standing and walking.
- `FootPlayer.InDuel` (fight or aura) turns off emotes, items, interact and challenges.
- Camera (`AuraView`, not VR): behind the waiting battler's shoulder on the performer, both sides;
  side-on between turns. `AuraHud` (layer 8): names and aura, round, clock, banners, the beat ring
  closing on each cut, grade popups, the verdict, best combos. `AuraPlanner` (layer 7): a card per move
  with ANSWERS / STALE / BITING, the strip, the estimate; frees the pointer while open.

## Controls

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| Next move (`aura_next`, timed) | Space, left click | A, RB | right trigger (sends RB) |
| Planner: add | click a card, arrows + Enter | stick / D-pad + A | stick + A |
| Take back the last (`aura_remove`) | Backspace | X | X |
| Suggest (`aura_suggest`) | G | R3 | R stick click |
| Lock in (`aura_lock`) | F | Y | Y |
| Cheer as a spectator | emote wheel: Cheer, Clap, Fist pump | same | same |

## Checks

- `AuraRulesTests` (tier 0): timing windows, plan limits, every score part, the worked examples,
  turn order, frozen misses, forfeit.
- `--emotecheck` (quick) and `--models,<dir> --modelsonly aura` for the poses.
- `tools/auranetcheck.sh` (net, port 7897): A opens, B sees the poses and the same verdict; B
  answers (answer ≥ 10) and leaves a cut frozen; the server refuses A's press in B's turn, a stamp
  5 s ahead and a nine-move plan; A forfeits. `SHOTS=1` windows B.

## Not done

- Composure ("don't laugh first") for the waiting battler; spectators get no HUD (the fight's gap
  too); the beat is a fixed 120 BPM, not the nearest radio's; no bot brain on `SuggestPlan`;
  E-at-the-player does not accept an aura challenge (E stays the fist fight's); VR cues by striking
  the pose for real (R4).
