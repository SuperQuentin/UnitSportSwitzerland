# Dance moves: joint-level spec for the procedural avatar, per style and move

**Conventions.** Author space (the space `HumanMeshBuilder.Rig` is written in): origin at the feet, metres, `+Y` up, `+Z` forward, `+X` right; `MeshScratch.Build` turns the mesh half a turn afterwards, so the figure's own right hand is **rig-L (`-X`)** and its left hand is **rig-R (`+X`)**; `s = -1` for L joints, `+1` for R joints. Mirroring a move is invisible to players, so asymmetric moves simply say rig-L / rig-R. Joints are the `Rig` fields (`HeadTop, HeadBase, Neck, Chest, Waist, Hip, Shoulder/Elbow/Wrist L/R, Hip/Knee/Ankle/Toe L/R, TorsoLean`). Standing spine offsets above `Hip` (from `GaitRig`): Waist `+0.155`, Chest `+0.410`, Shoulders `+0.510` at `x = ±0.18`, Neck `+0.590`, HeadBase `Neck+0.065`, HeadTop `Neck+0.255`; leg roots `x = ±0.09`; standing `Hip.Y = 0.935`; ankle height `0.085`. Bones (`HumanMeshBuilder` constants): `UpperArmLength 0.270`, `ForearmLength 0.245` (full arm **0.515 m**, not 0.57: clamp every wrist target to **0.50 m** from its shoulder), `ThighLength 0.435`, `ShinLength 0.415` (leg **0.85 m**: clamp hip-to-ankle to 0.845). Inputs per frame: `Style`, `Move` (index into the table below), `BeatPhase b` (0..1 inside the beat), `BarPhase r` (0..1 inside a 4-beat bar), `Weight` (0..1 ease in/out of the dance), `moving m` (0 standing .. 1 walking). Derived: **count clock `c = 4r`** (0..4; beat number `k = floor(c)`, so beat 1 is `k = 0`, and `b = frac(c)`). The result is a pure function of these inputs, so every peer computes the same pose. What the mesh can and cannot show: head **yaw is invisible** (the head is a box built by `UprightBasis` from the head axis; only head pitch/roll, i.e. the `HeadBase -> HeadTop` tilt, show), hand orientation (palm up/down) is invisible (the hand is an axis-aligned box), there is no body yaw (the figure cannot turn; "turns" are a chest twist `psi` of at most +/-0.9 rad, visible through the shoulder line and arms), and the pelvis/torso tubes are round, so pelvis yaw is invisible (pelvis roll/tilt shows through the two leg roots). Background: `avatars.md`, `avatar-meshes-authored-facing-z.md`, `item-arm-poses.md`.

## Move table

`MoveCount(style)` is the number of rows. Meme moves repeat across styles (the sections below are written once). Index is taken modulo `MoveCount(style)`.

| Style | Move index | Name |
|---|---|---|
| Pop (MoveCount 9) | 0 | SideStepClap |
| Pop | 1 | HipSway |
| Pop | 2 | ClapBackbeat |
| Pop | 3 | Carlton |
| Pop | 4 | Macarena |
| Pop | 5 | DiscoPoint |
| Pop | 6 | Floss |
| Pop | 7 | OrangeJustice |
| Pop | 8 | GangnamStyle |
| Rock (MoveCount 7) | 0 | Headbang |
| Rock | 1 | AirGuitar |
| Rock | 2 | FistPump |
| Rock | 3 | Bounce |
| Rock | 4 | ClapBackbeat |
| Rock | 5 | ArmWave |
| Rock | 6 | Pogo |
| Electronic (MoveCount 8) | 0 | Bounce |
| Electronic | 1 | FistPump |
| Electronic | 2 | RunningMan |
| Electronic | 3 | TStep |
| Electronic | 4 | Robot |
| Electronic | 5 | Sprinkler |
| Electronic | 6 | ArmWave |
| Electronic | 7 | Pogo |
| HipHop (MoveCount 8) | 0 | Bounce |
| HipHop | 1 | ShoulderLean |
| HipHop | 2 | Twerk |
| HipHop | 3 | Dab |
| HipHop | 4 | Griddy |
| HipHop | 5 | Moonwalk |
| HipHop | 6 | RunningMan |
| HipHop | 7 | Floss |
| Chill (MoveCount 6) | 0 | Sway |
| Chill | 1 | HipSway |
| Chill | 2 | ArmWave |
| Chill | 3 | ClapBackbeat |
| Chill | 4 | Moonwalk |
| Chill | 5 | Bounce |
| Folk (MoveCount 7) | 0 | FolkClap |
| Folk | 1 | HandsOnHipsSkip |
| Folk | 2 | SideStepClap |
| Folk | 3 | HipSway |
| Folk | 4 | ClapBackbeat |
| Folk | 5 | Macarena |
| Folk | 6 | GangnamStyle |

`MoveCount`: Pop 9, Rock 6, Electronic 7, HipHop 8, Chill 6, Folk 7. Tempo coverage: Sway/HipSway 60-110, ArmWave 60-130, Bounce 70-200, Headbang 90-200, FistPump 110-200, everything else inside 70-170, so 60-200 BPM is covered.

Crowd moves (#261), outside the tables: `Move = HumanMeshBuilder.GroupPogo` (1000) is **Pogo**,
`GroupJump` (1001) is **JumpTogether**.

## Picking, flowing and crowds (#261)

- **Who does what** (`FootPlayer.StepDance`): the move changes every `BarsPerMove` (2) bars. Each
  dancer picks its own (`hash(slot, style) ^ seed`, the seed an FNV-1a of the node name: the same on
  every peer, unlike `string.GetHashCode`), so a crowd is not a drill team. With two or more dancing
  to the same music (counted twice a second within `DanceRadius * 1.3`), one slot in three
  (`hash(slot, style) % 3 == 1`, no seed) is a crowd move, Pogo or JumpTogether, so everyone jumps
  on the same beat. Peers can disagree on the count for half a second; harmless.
- **Flow**: `DanceParams.PrevMove` and `MoveBlend` (beats into the slot / 0.9): the first beat of a
  slot mixes the channels of the move before into the new one (`Mix(DanceCh)`), no cut.
- **Groove** (every table move, not the jumps): the knees give 1.8 cm on each beat, the head nods
  0.045 rad into it, the shoulders bounce 1 cm a beat-fraction later, the hips sway 1.2 cm over two beats.
- **Pogo**: one jump per beat, off at b = 0.16, down at 0.94, 22 cm up (ankles raised with the hip,
  so the feet really leave the ground), a soft knee give on landing, one fist punched up every
  other beat. **JumpTogether**: beats 1-3 bounce deeper (3.5, 6, 8.5 cm) with the arms swinging back,
  beat 4 crouches 13 cm and jumps 34 cm with both arms thrown up, landing on the next bar's one.
- **Music to dance to** (`RadioManager.NearestMusic`): a playing radio in the world, or a player
  carrying one that plays (hand or back); the beat from `RadioBody.BeatOf(cd, startedAt, clock)`.

## Notation (used by every move section)

- **Helpers** (turn-based: `sinT(x) = sin(2*pi*x)`, `cosT(x) = cos(2*pi*x)`): `Dip(b) = (1+cosT(b))/2` (1 on the beat, 0 on the offbeat); `Hop(b) = sinT(b/2)` (0 on the beat, 1 mid-beat); `Smooth(x) = x*x*(3-2x)` with x clamped to 0..1; `D = sinT(c/2)` and `E = cosT(c/2)` (period 2 beats: D is +1 mid-beat 1 and -1 mid-beat 2; E is +1 on beats 1 and 3, -1 on beats 2 and 4); `Sq(x) = clamp(3x, -1, 1)` (soft square wave); `SawNod(b)`: for `b < 0.75` `Lerp(0.60, -0.25, Smooth(b/0.75))`, else `Lerp(-0.25, 0.60, Smooth((b-0.75)/0.25))` (the head is lowest exactly on the beat).
- **Channels** each move fills (defaults in brackets): pelvis shift `P = (px,py,pz)` added to the rest `Hip` [0]; pelvis tilt `t` in metres, leg root `y += s*t` [0]; lean `theta` rad (+ = top goes `+Z`) [rest = gait lean, 0 standing]; roll `phi` (+ = top goes `+X`) [0]; twist `psi` (+ = chest front turns toward `+X`) [0]; head nod `thN` (+ = looks down) and head roll `phH` [0]; shrug `shL, shR` metres added to shoulder `Y` [0]; arms; legs.
- **Spine chain** (every move): for a joint with rest lateral `x0` and height `o` above the hip, `v = (x0, o*cos(theta), o*sin(theta))`; then roll `(x*cos(phi) + y*sin(phi), -x*sin(phi) + y*cos(phi))`; then yaw by `psi*k` (`(x,z) -> (x*cos + z*sin, -x*sin + z*cos)`) with `k`: Waist 0.25, Chest 0.6, Shoulders and Neck 1.0; joint = `Hip + v`. Head: `thH = 0.35*theta + thN` (as `GaitRig`, the head carries a third of the lean), `u = (sin(phH), cos(phH)*cos(thH), cos(phH)*sin(thH))`, `HeadBase = Neck + 0.065u`, `HeadTop = Neck + 0.255u`. Set `TorsoLean = theta`. `T` below is the torso rotation (pitch, roll, yaw by the full `psi`).
- **Arm anchors** (targets for `Limb.Solve(shoulder, wrist, 0.270, 0.245, hint)`): `o(out,up,fwd)`: `wrist = Shoulder_s + T*(s*out, up, fwd)` (out = away from the midline, mirrored per side); `O(x,y,z)`: same anchor, unmirrored; `H(x,y,z)`: `wrist = Hip + T*(x,y,z)`. Elbow hint `h(out,up,fwd)` is mirrored like `o`; presets `hLow = h(0.6,-0.6,-0.2)` (the `ApplyArms` default), `hOut = h(1,0,-0.3)`, `hUp = h(0.5,0.9,-0.2)`. **`ArmChain(s; a1,g1; a2,g2)`** places elbow and wrist directly (bones keep their length, no IK): `d(a,g) = (s*cos a*cos g, sin a, cos a*sin g)` in `T`, `Elbow = Shoulder + 0.270*d(a1,g1)`, `Wrist = Elbow + 0.245*d(a2,g2)`; `a` is elevation (+ up), `g` azimuth (0 out to the side, pi/2 forward, pi toward the midline, -pi/2 backward).
- **Presets** (`o(...)` triples): `HANG = o(0.04,-0.49,0.02)`; `AKIMBO = o(0.05,-0.40,0.06)` with `hOut` (hands on hips, elbows flared); `CLAP_C = o(-0.15,-0.10,0.30)` (hands meeting in front of the chest, hint `h(0.5,-0.6,-0.2)`); `FWD = o(-0.03,0,0.47)`.
- **Key-framed arms.** `Seq(x, [v0..vN-1])` = cyclic interpolation, `i = floor(x)`, value `Lerp(v_i, v_(i+1 mod N), Smooth(frac(x)))`; `SeqHold(x, vs, a)` = same but the transition happens in the first fraction `a` of each slot, then holds (`Smooth(clamp(frac(x)/a, 0, 1))`). Crossfade between two segments of one move over 0.25 beat with `Smooth`.
- **Legs (standing only).** `HipRoot(s) = (px + s*0.09, hipY + py + s*t, pz)`; knee from `Limb.Solve(HipRoot, Ankle, 0.435, 0.415, (s*0.35,0,1))` (use `(s*0.6,0,1)` when ankle `|x| >= 0.2`, so the knees splay over the toes). `Planted(w, d, a=0.15)`: ankle `(s*w, 0.085, 0)`, `py = -d` (squat depth), toe `= Ankle + (s*0.145*sin a, -0.045, 0.145*cos a)` (toes turned out by `a`). Lifted foot: add to ankle `y`, toe `= Ankle + (0, -0.045+0.045*liftFrac, 0.145)`. Heel up (ball on ground): ankle `y = 0.085+h` (`h <= 0.07`), toe `= (x, 0.040, ankle.z+0.13)`. **LegFit** (mandatory last step): per leg `hipYmax = ankle.y + sqrt(0.845^2 - dx^2 - dz^2)` with `dx, dz` the horizontal offsets from the leg root to the ankle; `hipY = min(hipY, hipYmax_L, hipYmax_R)`. Without it wide stances and long slides give unreachable ankles.
- **Weight and moving.** With `we = Smooth(Weight)`: every channel `X = X_rest + we*(X_dance - X_rest)` (wrist and ankle targets too, *before* the IK), where `rest` is the rig the player would have without dancing (`GaitRig(speed, phase)`; standing when `m = 0`). **Moving variant:** legs always follow `GaitRig` and the dance never touches them when `m = 1` (`legs = Lerp(danceLegs, gaitLegs, Smooth(m))`); the pelvis offset is applied to `Hip/Waist/Chest/...` only (leg roots stay on the gait, which the round pelvis tube hides) and is clamped to `|px|, |py|, |pz| <= 0.04`; every other channel amplitude (`theta`, `phi`, `psi`, nods, arm offsets relative to the gait arm) is scaled by `A = 1 - m*(1-kMov)`; `kMov` and the arm policy are given per move. `theta` adds to the gait's own lean.
- **Clearance.** Never leave a wrist with `|x| < 0.12` and `|z - Chest.z| < 0.10` (inside the torso). Practical rule: keep every wrist at least **0.21 m** from the segment `Hip..Neck` (chest radius 0.158 + hand half depth 0.04); hands on hips stay at `|x| >= 0.23`; hands beside the head stay at `|x| >= 0.12`. The presets above satisfy this.

## Move: Twerk

- **Name / origin:** Twerk, New Orleans bounce music, first recorded as a word in 1993 (https://en.wikipedia.org/wiki/Twerking). Posture (feet wider than shoulders, knees bent, hands on knees or hips, thrust the hips back and release) from tutorial summaries.
- **Tempo range:** 70-120 BPM (two pumps per beat, 2.3-4 Hz).
- **Counts:** The stance is held the whole bar. A pump lands on every beat (1, 2, 3, 4) and on every "&" between them; the on-beat pump is the big one (amplitude `a = 0.75 + 0.25*cosT(b)`, 1.0 on the beat, 0.5 on the "&"). Optional hip lift alternates side each beat (L on 1 and 3, R on 2 and 4).
- **Joint spec** (`q = (1 + cosT(2b))/2`: 1 on the beat and on the "&", 0 between; `a` as above):
  - Pelvis: `px = 0`; `py = -0.235 - 0.035*q*a` (hip height about 0.70 m, dropping further on each pump); `pz = -0.12 - 0.08*q*a` (hips sit back, thrust further back on the pump, forward on the release). Tilt `t = 0.02*E*a` (alternate hip lift, optional).
  - Torso: `theta = 0.87 - 0.04*q*a` (50 degrees forward, chest rises slightly as the pelvis goes back); `phi = 0`; `psi = 0`. Check: shoulders land near `(±0.18, 1.03, 0.23)`.
  - Head: `thN = -0.45` (looks forward, not at the floor); `phH = 0`.
  - Arms (hands on knees, in world space): `Wrist_s = Knee_s + (0, 0.09, -0.02)` where `Knee_s` is the solved knee of the same side this frame (so the hands ride the pump); elbow hint `hOut` (elbows locked out and slightly back); if `|Wrist - Shoulder| > 0.50` pull the target toward the shoulder to 0.50 (at 50 degrees lean the distance is about 0.49). Option: one hand on the hip (`AKIMBO`) for the side whose hip lifts.
  - Legs: `Planted(0.30, d, a = 0.35)` with `d` from `py` above (feet wide, toes out, knees splayed with hint `(s*0.6, 0, 1)`); LegFit (hip Y about 0.70 against a 0.30 half stance needs about a 0.615 m vertical reach, well inside 0.845).
- **Moving variant:** legs keep the walking gait. `kMov = 0.4`: pelvis `pz = -0.03 - 0.03*q*a`, `py = -0.02*q*a` (both inside the 0.04 clamp); `theta = 0.35` added to the gait lean; hands go to `AKIMBO` (no knees to rest on; blend the arms over 0.3 beat); `thN = -0.15`.

## Move: Dab

- **Name / origin:** Dab (https://en.wikipedia.org/wiki/Dab_(dance)): lean into the crook of a bent, upward slanted arm while the other arm extends straight in a parallel direction; popular from 2014-2015.
- **Tempo range:** 70-130 BPM (one dab per 2 beats).
- **Counts:** Beat 1: dab to `sigma = +1` (toward `+X`), held through beat 2; beat 3: dab to `sigma = -1`, held through beat 4. `SeqHold` with `a = 0.25` (a quick snap, then hold); a small bounce `py -= 0.015*Dip(b)` on beats 2 and 4.
- **Joint spec** (`sigma = +1` for `c < 2`, else `-1`):
  - Pelvis: `px = -sigma*0.03`; `py = -0.07`.
  - Torso: `theta = 0.20`; `phi = sigma*0.10`; `psi = sigma*0.25`.
  - Head: `thN = 0.50` (face into the crook); `phH = sigma*0.25`.
  - Arms: straight arm = the arm with `s = sigma`: `wrist = o(0.35, 0.33, 0.05)`, hint `hOut`. Bent arm = `s = -sigma`: `wrist = o(-0.28, 0.16, 0.26)` (hand in front of the face, toward the side the head goes), hint `h(0.3, 1.0, 0.2)` (elbow up, which makes the crook the face tucks into). The two arms swap role at the switch; blend the wrists over the snap, crossing in front (raise `fwd` of the arm that goes across first).
  - Legs: `Planted(0.17, 0.07)` with weight on the `-sigma` leg (`px` above); toes out `a = 0.15`.
- **Moving variant:** legs walk. `kMov = 0.8`; arms as above; `px = 0` (the clamp applies anyway); head nod as above.

## Move: Floss

- **Name / origin:** Floss (https://en.wikipedia.org/wiki/Floss_(dance)): clenched fists swing from behind the body to the front on alternating sides while the hips swing the other way.
- **Tempo range:** 100-140 BPM.
- **Counts:** One swing per beat: the arms reach the `+X` side mid-beat 1, the `-X` side mid-beat 2, `+X` mid-beat 3, `-X` mid-beat 4; the hips go the opposite way. The arms pass the centre line (one in front of the belly, the other behind the back) on each beat.
- **Joint spec** (`D`, `E` over the 2-beat cycle):
  - Pelvis: `px = -0.07*D` (hips against the arms); `py = -0.06 - 0.02*Dip(b)` (knees bent, dip on every swing); `pz = 0`.
  - Torso: `theta = 0.05`; `phi = 0.08*D` (shoulders go with the arms); `psi = 0`.
  - Head: `thN = 0`, `phH = 0.05*D`.
  - Arms (straight arms, fists, anchored at the hip so the circling path stays outside the body): `Wrist_s = H(0.20*D, 0.22, s*0.22*E)`; hint `hLow`. Both hands share `x = 0.20*D`; `s*0.22*E` puts rig-R in front (+Z) when `E > 0` and rig-L behind, swapping at the extremes where `E = 0`. The path is an ellipse `(0.20, 0.22)` about the body axis, radius at least 0.29 m, so the hands never enter the torso; at the extremes `|Wrist - Shoulder|` is 0.48.
  - Legs: `Planted(0.15, d)` with `d` from `py`; free-foot heel lift `ankle.y += 0.02*max(0, -s*D)`.
- **Moving variant:** legs walk. `kMov = 0.7` (arms circle at 70 %: `0.14*D`, `0.154*E`), `px = -0.03*D`, arms replace the gait arms (blend 1).

## Move: Carlton

- **Name / origin:** The Carlton (Alfonso Ribeiro, The Fresh Prince of Bel-Air): https://steezy.co/posts/how-to-do-the-carlton and https://en.wikipedia.org/wiki/Carlton_Banks (hips lead, arms swing up and back toward the step side; a finger-flutter finish).
- **Tempo range:** 90-130 BPM.
- **Counts:** Beats 1-2: side sway, one step per beat (R on 1, L on 2), arms swing up and back in the step direction. Beat 3: both arms rise above the head. Beat 4: the rig-R hand flutters down from above the head to the middle of the chest while the feet stay planted and the other hand rests on the hip. The bar repeats.
- **Joint spec** (segment A `c in [0,2)`, segment B `c in [2,4)`; crossfade at `c = 2` and `c = 4` over 0.25 beat):
  - Pelvis: A: `px = 0.07*D`, `py = -0.04 - 0.02*Dip(b)`; B: `px = 0`, `py = -0.04 - 0.015*Dip(b)`.
  - Torso: A: `phi = 0.10*sinT(c/2 - 0.06)` (shoulders arrive last), `psi = 0.12*D`, `theta = 0.04`; B: all zero except `theta = 0.04`.
  - Head: `thN = 0.05*Dip(b)`; `phH = -0.10*D` in A.
  - Arms: A: both arms swing in the step direction, unmirrored: `dl = sinT(c/2 + 0.04)`, `Wrist_s = O(0.42*sin(1.2*dl), -0.42*cos(1.2*dl), 0.24)`, hint `hLow` (the hands rise to shoulder-height-minus-0.17 at the extremes, radius 0.484 m). B: for `c in [2,3)` both wrists go to `o(-0.05, 0.47, 0.03)` (straight up); for `c in [3,4)` rig-R: `Seq` from that to `o(-0.14,-0.12,0.26)` (chest, with a flutter `wrist.y += 0.012*sinT(6*c)`), rig-L goes to `AKIMBO`.
  - Legs: A: weight shift `S = Sq(D)` (+1 = weight on R): ankle `x = s*0.098 + s*0.06*(1 + s*S)` (the weight foot steps wide, the other closes in), free foot `ankle.y += 0.03*max(0, -s*S)*Hop(b)`; B: `Planted(0.14, 0.04)`.
- **Moving variant:** legs walk. Segment A arm swing at `kMov = 0.7`; segment B raise and flutter unchanged; `px` clamp 0.04.

## Move: GangnamStyle

- **Name / origin:** Gangnam Style (PSY, 2012, horse-riding and lasso, https://en.wikipedia.org/wiki/Gangnam_Style; "pretend to ride a galloping horse").
- **Tempo range:** 110-145 BPM (the song is at 132 BPM).
- **Counts:** Beats 1-2: horse ride, a hop on every beat with alternating feet (R forward on 1, L forward on 2), hands held in front like reins. Beats 3-4: lasso, one overhead circle of the rig-R hand per beat while the hop continues.
- **Joint spec** (`Hop(b)`, `D`; segment crossfade over 0.25 beat):
  - Pelvis: `py = -0.06 + 0.05*Hop(b)` (lands on the beat, airborne mid-beat); `px = 0`.
  - Torso: `theta = 0.10`; `phi = 0.04*D`; `psi = 0.10*D`.
  - Head: `thN = 0.05`; `phH = 0`.
  - Arms: segment A (`c < 2`): both hands as reins: `wrist = o(-0.12, -0.22 + 0.04*Hop(b) + 0.03*s*D, 0.32)` (hands nearly together in front of the belly, bobbing), hint `hLow`. Segment B (`c >= 2`): rig-L keeps the reins; rig-R lasso: `alpha = 2*pi*c` (one turn per beat), `wrist = O(0.15*cos(alpha), 0.42, 0.15*sin(alpha))` anchored at its own shoulder (a circle of radius 0.15 above the shoulder, 0.446 m from it, hand above the head height), hint `hUp`.
  - Legs: `Planted(0.16, 0.06)` plus the gallop: `ankle.z = 0.12*s*E` so rig-R is forward (+Z) on beats 1 and 3 and rig-L on beats 2 and 4; both feet `ankle.y += 0.04*Hop(b)` (the hop), toes `a = 0.1`; LegFit.
- **Moving variant:** legs walk. `kMov = 0.6`: hop `py` amplitude 0.02, torso amplitudes scaled, arms as above (blend 1).

## Move: Macarena

- **Name / origin:** Macarena (Los del Rio, 1993), simplified to eight counts for the 1996 video (https://en.wikipedia.org/wiki/Macarena_(song); also https://www.linedance.com/dance/144800/macarena). Arm sequence from tutorial summaries (recorded in the research notes at the end).
- **Tempo range:** 70-115 BPM.
- **Counts:** Eight gestures, each held until the next: 1 rig-L arm straight forward, 2 rig-R arm forward, 3 rig-L palm up, 4 rig-R palm up, 5 rig-L hand to the rig-R shoulder, 6 rig-R hand to the rig-L shoulder, 7 rig-L hand behind the ear, 8 rig-R hand behind the ear; then both hands drop to the hips and the cycle restarts. **Clock:** with the fixed inputs use double time, gesture `j = floor(2c)` (0..7) and `f = frac(2c)`: one gesture per half beat (0.26-0.43 s at 70-115 BPM). If an optional bar-parity bit `P` (alternate bars) is supplied use `j = 4P + floor(c)` and `f = b`: one gesture per beat, the authentic speed (prefer this).
- **Joint spec:** each arm moves to the target of the current gesture during the first 0.45 of the slot (`Smooth(min(f/0.45, 1))`) and holds. Targets (`o(...)`): `OUT = FWD` hint `hLow`; `UP = o(-0.03, 0.04, 0.47)` (palm-up flip is not visible: only a slight lift); `CROSS = o(-0.32, -0.02, 0.22)` hint `h(0,-1,0.3)` (hand at the opposite shoulder, 0.39 m, crossing in front of the chest); `EAR = o(-0.05, 0.16, -0.04)` hint `h(1,0.3,-0.3)` (hand beside the ear, `|x| = 0.13`, outside the head box); `HIP = AKIMBO`. rig-L sequence: j0 `OUT`, j1 hold, j2 `UP`, j3 hold, j4 `CROSS`, j5 hold, j6 `EAR`, j7 hold. rig-R sequence: j0 `HIP`, j1 `OUT`, j2 hold, j3 `UP`, j4 hold, j5 `CROSS`, j6 hold, j7 `EAR`. The two `CROSS` hands pass each other at different `fwd` (rig-L 0.22, rig-R 0.22 + 0.05 during the crossing) so they never overlap.
  - Pelvis: `px = 0.04*sinT(c/2)` (hip sway each beat; in the original hips roll between the arm sets and the whole body makes a quarter turn, which the mesh cannot show); `py = -0.02*Dip(b)`.
  - Torso: `theta = 0`; `phi = -0.04*sinT(c/2)`; `psi = 0.08*D` (a slight turn toward the moving hand).
  - Head: `thN = 0.04*Dip(b)`; `phH = 0.05*D`.
  - Legs: `Planted(0.13, 0.03)`; free-foot heel lift `0.02*max(0, -s*sinT(c/2))`.
- **Moving variant:** legs walk. `kMov = 0.6`; the arm gestures stay at full size (blend 1), pelvis `px = 0.03*...` inside the 0.04 clamp.

## Move: OrangeJustice

- **Name / origin:** Orange Justice (Fortnite emote, 2018; https://en.wikipedia.org/wiki/Orange_Justice): knees bent, arms pumping in a criss-cross, then a shrug and a clap above the head.
- **Tempo range:** 100-140 BPM.
- **Counts:** Beats 1-3 (`c < 3`): criss-cross pump, hands alternate sides each beat, knees bending on every beat. Beat 4 first half (`c in [3, 3.5)`): shrug with the hands at the shoulders; second half (`c in [3.5, 4)`): clap above the head. Loop.
- **Joint spec** (crossfade at segment changes over 0.2 beat):
  - Pelvis: `py = -0.06 - 0.05*Dip(b)` (deeper on the beat); `px = 0`.
  - Torso: `theta = 0.05`; `phi = 0.04*D`; `psi = 0.10*D` (shoulders follow the crossing).
  - Head: `thN = 0.05*Dip(b)`; `phH = 0`.
  - Arms, `c < 3`: `Wrist_s = H(-s*0.18*D, 0.38 + 0.04*Dip(b), 0.30 + 0.08*s*E)` (hands cross the midline in opposite directions, offset in `Z` by 0.16 m at the crossing so they never touch; at the extremes `|Wrist - Shoulder| = 0.49`), hint `hLow`. Shrug `c in [3,3.5)`: `sh = 0.05*Smooth(...)` (both `shL`, `shR`), `wrist = o(0.05,-0.05,0.22)`, hint `hOut`. Clap `c in [3.5,4)`: `sh` back to 0, `wrist = o(-0.16, 0.44, 0.03)` (hands meet at `x = ±0.02`, 0.95 m above the hip, above the head box).
  - Legs: `Planted(0.15, d)` with `d` from `py`; heel lift on the clap `ankle.y += 0.03*Smooth`.
- **Moving variant:** legs walk. `kMov = 0.7` (pump `0.13`, `0.27`), the shrug and clap unchanged, arms blend 1.

## Move: SideStepClap

- **Name / origin:** Step-touch with clap, the basic pop/line-dance side step (generic, no single source).
- **Tempo range:** 90-140 BPM.
- **Counts:** Beat 1: step to rig-R (weight over R). Beat 2: rig-L foot touches in, clap. Beat 3: step to rig-L. Beat 4: rig-R foot touches in, clap.
- **Joint spec** (`S = Sq(sinT(r))`, +1 = weight on R during beats 1-2, -1 during beats 3-4):
  - Pelvis: `px = 0.06*sinT(r)`; `py = -0.03 - 0.02*Dip(b)`.
  - Torso: `phi = -0.04*sinT(r)`; `psi = 0`; `theta = 0.03`.
  - Head: `thN = 0.03*Dip(b)`.
  - Arms: claps on 2 and 4 with `cl = ((1 - cosT(c/2))/2)^2` (1 at c = 1 and 3): `wrist = o(0.20 - 0.35*cl, -0.05 - 0.05*cl, 0.30)` (open at about 0.76 m apart, closed at `x = ±0.03`, at chest height), hint `hLow`.
  - Legs: ankle `x = s*0.098 + s*0.06*(1 + s*S)` (the weight foot steps out to 0.218, the free foot closes to 0.098), free-foot lift `0.05*max(0, D)*[s*S < 0]` (lift on beats 1 and 3, land on the clap), `Planted` toes `a = 0.15`; LegFit.
- **Moving variant:** legs walk. `kMov = 0.6`; claps unchanged (blend 1), `px = 0.03*sinT(r)`.

## Move: HipSway

- **Name / origin:** Hip sway, the basic weight-shifting sway (generic, no single source).
- **Tempo range:** 60-110 BPM.
- **Counts:** One sway per beat: pelvis to rig-R on beats 1 and 3, to rig-L on beats 2 and 4.
- **Joint spec:**
  - Pelvis: `px = 0.06*E`; `py = -0.02`; tilt `t = 0.015*E`.
  - Torso: `phi = -0.07*E` (ribcage counter-shifts, a shallow S); `psi = 0.10*D`; `theta = 0`.
  - Head: `phH = 0.05*E`; `thN = 0`.
  - Arms: `HANG` with a slight counter-swing `fwd += 0.05*s*D` (hands swing against the hips), hint `hLow`.
  - Legs: `Planted(0.11, 0.02)`; free foot heel lift `ankle.y += 0.02*max(0, -s*E)`.
- **Moving variant:** legs walk. `kMov = 0.5`: `px = 0.03*E`, `phi = -0.035*E`, `psi = 0.05*D`; arms stay on the gait (blend 0).

## Move: ClapBackbeat

- **Name / origin:** Clap on 2 and 4, the backbeat clap (generic crowd clapping, no single source).
- **Tempo range:** 70-140 BPM.
- **Counts:** Claps on beats 2 and 4 only; the hands swing apart after each clap and are open on 1 and 3.
- **Joint spec** (`cl = ((1 - cosT(c/2))/2)^2`, 1 at c = 1 and 3):
  - Pelvis: `py = -0.02*Dip(b)`; `px = 0.03*E`.
  - Torso: `phi = -0.03*E`; `theta = 0.04`.
  - Head: `thN = 0.04*Dip(b)`.
  - Arms: `wrist = o(0.20 - 0.35*cl, -0.05 - 0.05*cl, 0.30)` (open to about 0.76 m apart, closed to `x = ±0.03`), hint `hLow`.
  - Legs: `Planted(0.11, 0.03)`; no foot events.
- **Moving variant:** legs walk. `kMov = 0.8`; the clap stays (blend 1), torso `0.8x`.

## Move: DiscoPoint

- **Name / origin:** The disco point (Saturday Night Fever, 1977, https://en.wikipedia.org/wiki/Saturday_Night_Fever for the film; the exact pose is not described there, see the research notes): one arm points at the ceiling on the diagonal, the other hand on the hip, the pelvis pushed out.
- **Tempo range:** 100-130 BPM.
- **Counts:** Beat 1: rig-L arm snaps up-diagonal (point), rig-R hand on hip. Beat 2: the arm sweeps down across the body (point at the floor). Beat 3: rig-R arm up, rig-L hand on hip. Beat 4: rig-R arm sweeps down. `SeqHold` with `a = 0.3`.
- **Joint spec** (`sigma = -1` for `c < 2` (pointing arm is rig-L), `+1` for `c >= 2`):
  - Pelvis: `px = -sigma*0.05` (hip pushed out to the side of the free hand); `py = -0.04`.
  - Torso: `phi = sigma*0.10` (leans toward the pointing side); `theta = 0`; `psi = sigma*0.10`.
  - Head: `phH = -sigma*0.10`; `thN = 0`.
  - Arms: pointing arm (`s = sigma`): count A (`c mod 2` in `[0,1)`) `o(0.30, 0.38, 0.05)` (0.49 m, up and out), count B (`[1,2)`) `o(-0.20, -0.40, 0.22)` (down and across to the opposite hip, in front of the pelvis: `x` about 0.02, `z` 0.22, clear of the 0.14 hip tube); hint `hOut`. The other arm: `AKIMBO`.
  - Legs: `Planted(0.14, 0.03)` with the weight on the leg opposite the pointing arm: heel lift `ankle.y += 0.03` on the pointing side (`s = sigma`); pelvis tilt `t = sigma*0.015`.
- **Moving variant:** legs walk. `kMov = 0.7`: arms unchanged (blend 1), `px` clamp 0.04.

## Move: Headbang

- **Name / origin:** Headbanging (https://en.wikipedia.org/wiki/Headbanging): shaking the head in rhythm; the up-and-down form is used here.
- **Tempo range:** 90-200 BPM (one nod per beat).
- **Counts:** The head is lowest exactly on every beat (1, 2, 3, 4) and rises between; the torso folds slightly on the beat.
- **Joint spec:**
  - Pelvis: `py = -0.03*Dip(b)`; `px = 0`.
  - Torso: `theta = 0.20 + 0.15*Dip(b)`; `phi = 0.04*D`; `psi = 0`.
  - Head: `thN = SawNod(b)` (+0.60 on the beat, -0.25 just before the whip); `phH = 0.10*D`.
  - Arms: `wrist = o(0, -0.30 - 0.06*Dip(b), 0.28)` (fists forward at belt level, pumping down with the nod), hint `hLow`.
  - Legs: `Planted(0.22, 0.08, a = 0.2)` (wide rock stance); heel lift on the upbeat `ankle.y += 0.015*(1 - Dip(b))`.
- **Moving variant:** legs walk. `kMov = 0.7`: the head nod stays at full size (`thN` is not scaled, the clearest read at a run), `theta` reduced to `0.12 + 0.08*Dip(b)`, arms blend 0.5 over the gait arms.

## Move: AirGuitar

- **Name / origin:** Air guitar (https://en.wikipedia.org/wiki/Air_guitar): exaggerated strumming and fretting motions; the windmill strum is the common flourish (detail from tutorial knowledge, not from that page).
- **Tempo range:** 80-170 BPM.
- **Counts:** Beats 1-3: strumming hand pumping down on each beat and "&" while the fretting hand slides along the neck on 1 and 3. Beat 4: the strumming arm makes one full windmill circle. Loop.
- **Joint spec** (strum arm is rig-L, fret arm is rig-R; `Smooth` crossfades at the windmill edges over 0.15 beat):
  - Pelvis: `py = -0.06 - 0.03*Dip(b)`; `pz = 0`.
  - Torso: `theta = 0.12 + 0.05*Dip(b)`; `psi = 0.15` (body angled, guitar stance); `phi = -0.05`.
  - Head: `thN = 0.25*Dip(b)`; `phH = 0.08*D`.
  - Arms: fret arm (rig-R): `wrist = o(0.12, -0.08, 0.42 + 0.05*D)`, hint `hLow`. Strum arm (rig-L): `c < 3`: `wrist = o(-0.18, -0.36 - 0.06*cosT(2b), 0.26)` (low on the beat and the "&", high between), hint `hLow`; `c in [3,4)`: windmill angle `al = 2*pi*(c - 3)`, `wrist = o(0.05, -0.46*cos(al), 0.46*sin(al))` (0.463 m; at `al = 0` the hand hangs, at `pi/2` it is forward, at `pi` straight up, at `3*pi/2` behind the back, clear of the torso everywhere).
  - Legs: `Planted(0.25, 0.12)` with a power stance: `ankle.z = +0.15` for rig-R, `-0.10` for rig-L; knee bounce is the `py` above.
- **Moving variant:** legs walk. `kMov = 0.7`; strumming and fretting stay, the windmill is skipped when `m > 0.5` (strum continues through beat 4).

## Move: FistPump

- **Name / origin:** Fist pump (https://en.wikipedia.org/wiki/Fist_pump): a raised fist drawn down in a vigorous motion, here pumped in time, alternating arms.
- **Tempo range:** 110-200 BPM.
- **Counts:** rig-L fist is up on beats 1 and 3, rig-R fist is up on beats 2 and 4; each arm is at chest level on the opposite beat.
- **Joint spec** (per arm `pA = (1 + cosT((c - c0)/2))/2`, `c0 = 0` for rig-L and `1` for rig-R):
  - Pelvis: `py = -0.04*Dip(b)`; `px = 0`.
  - Torso: `theta = 0.05`; `phi = -0.06*E` and `psi = -0.12*E` (on beats 1 and 3 `E > 0`, the raised fist is rig-L at `-X`, so the torso leans and turns toward `-X`; the opposite on beats 2 and 4).
  - Head: `thN = 0.06*Dip(b)`.
  - Arms: `wrist = Lerp(o(0.10,-0.05,0.25), o(0.14,0.42,0.12), pA)` (chest level to up-and-out, 0.46 m at the top), hint `hUp` at the top and `hLow` at the bottom (blend with `pA`).
  - Legs: `Planted(0.15, 0.04)`; heels `ankle.y += 0.015*(1 - Dip(b))`.
- **Moving variant:** legs walk. `kMov = 0.6`; the pump stays at full size (blend 1), bounce `py` clamp 0.04.

## Move: Bounce

- **Name / origin:** Knee bounce on the beat, the basic four-on-the-floor groove (generic, no single source).
- **Tempo range:** 70-200 BPM.
- **Counts:** One bounce per beat: down (knees bent) on the beat, up on the "&".
- **Joint spec:**
  - Pelvis: `py = -0.07*Dip(b)`; `px = 0`.
  - Torso: `theta = 0.05 + 0.05*Dip(b)`; `phi = 0.03*D`; `psi = 0`.
  - Head: `thN = 0.10*Dip(b)`; `phH = 0.04*D`.
  - Arms: `wrist = o(0.10, -0.30 - 0.05*Dip(b), 0.22)` (elbows bent, hands at the belt pumping slightly), hint `hLow`.
  - Legs: `Planted(0.13, 0.07*Dip(b))`; heels rise as the body rises: `ankle.y += 0.03*(1 - Dip(b))`.
- **Moving variant:** legs walk. `kMov = 0.5`: `py = -0.03*Dip(b)`, arms blend 0.6 (hands pump over the gait arms).

## Move: ArmWave

- **Name / origin:** The arm wave / ripple (a crest traveling from one hand through elbow and shoulders to the other hand); no sourcable description was found (the Wikipedia wave page is not reachable), so the joint timing here is authored.
- **Tempo range:** 60-130 BPM (one wave per bar).
- **Counts:** The crest leaves the rig-L hand around beat 1 and reaches the rig-R hand around beat 4: nodes at `r = 0.05` (L hand), `0.20` (L elbow), `0.35` (L shoulder), `0.55` (R shoulder), `0.70` (R elbow), `0.85` (R hand).
- **Joint spec** (`Bump(r, t) = exp(-(dist/0.08)^2)`, `dist` the wrapped distance `min(|r-t|, 1-|r-t|)`; per arm `Bh, Be, Bsh` the bumps at its hand, elbow, shoulder node times):
  - Pelvis: `py = -0.01*Dip(b)`; `px = 0`.
  - Torso: `phi = 0.04*sinT(r)`; others 0.
  - Head: `phH = 0.05*sinT(r)`.
  - Arms: `ArmChain(s; a1, g1 = 0; a2, g2 = 0)` (arms out to the sides) with `a1 = 0.12 + 0.45*Be`, `a2 = 0.12 + 0.60*Bh - 0.35*Be`; shrug `sh_s = 0.035*Bsh`.
  - Legs: `Planted(0.13, 0.02)`.
- **Moving variant:** legs walk. `kMov = 1` (arms only; the wave is an upper-body move), torso bits at `0.5`.

## Move: Robot

- **Name / origin:** The robot (https://en.wikipedia.org/wiki/Robot_(dance)): movements started and stopped abruptly ("dimestops") to suggest motors.
- **Tempo range:** 80-140 BPM.
- **Counts:** A new pose "hits" on every beat and holds (transition in the first 0.12 beat, linear, no easing): beat 1 goalposts, beat 2 pose B, beat 3 tray, beat 4 pose C.
- **Joint spec** (`SeqHold(c, [A,B,D,C], 0.12)` with a linear, not `Smooth`, ramp; angles via `ArmChain(s; a1,g1; a2,g2)`):
  - Poses: A (goalposts, both arms): `(0,0; pi/2,0)`. B: rig-L `(0, pi/2; pi/2, 0)` (upper arm forward, forearm up), rig-R `(-1.35, 0; 0, pi/2)` (upper arm down and slightly out, forearm forward). D (tray, both arms): `(-1.35, 0; 0, pi/2)`. C: mirror of B (swap the roles of rig-L and rig-R).
  - Pelvis: `py = 0` (stiff, no bounce); `px = 0`.
  - Torso: `psi`: A 0, B +0.30, D 0, C -0.30 (hit with the pose); `theta = 0`; `phi = 0`.
  - Head: `phH`: A 0, B +0.15, D 0, C -0.15 (tilt tick); `thN = 0`.
  - Legs: `Planted(0.12, 0.02)`, no motion (locked joints).
- **Moving variant:** legs walk. `kMov = 0.8`; arms and torso ticks stay, the head tick unchanged.

## Move: RunningMan

- **Name / origin:** Running man (https://en.wikipedia.org/wiki/Running_man_(dance); MC Hammer's 1980s Oakland dance): steps forward then slides the front foot back while the fists pump horizontally. Footwork detail is from the same page and tutorial knowledge.
- **Tempo range:** 100-170 BPM (one step per beat).
- **Counts:** Each beat one leg lifts the knee forward and steps while the other foot slides back; rig-R knee up on beats 1 and 3, rig-L knee up on 2 and 4; fists pump opposite to the legs.
- **Joint spec** (per leg `ph = frac(c/2 + (s == +1 ? 0 : 0.5))`):
  - Pelvis: `py = -0.04 - 0.03*Dip(b)`; `px = 0`.
  - Torso: `theta = 0.12`; `phi = 0`; `psi = 0.10*D` (rig-R knee up on beats 1 and 3, so the rig-L shoulder comes forward, which turns the chest front toward `+X`).
  - Head: `thN = 0`.
  - Arms: `wrist_s = o(0.03, -0.25, 0.05 + 0.26*a_s)` with `a_s = sinT(c/2 + (s == -1 ? 0 : 0.5))` (forward up to +0.31 from the shoulder, back to -0.21; clear of the body), hint `h(0.35,-0.25,-1)` (the gait's elbow hint: back and slightly out).
  - Legs: for `ph in [0, 0.5)` the foot lifts and swings forward: `ankle.z = Lerp(-0.05, 0.20, Smooth(2*ph))`, `ankle.y = 0.085 + 0.22*sinT(ph)`; for `ph in [0.5, 1)` the foot is planted and slides back linearly: `ankle.z = Lerp(0.20, -0.05, 2*(ph - 0.5))`, `ankle.y = 0.085`; feet at `x = s*0.10`; LegFit (the planted foot at `z = 0.20` would need `0.873 > 0.845` so LegFit lowers the hips).
- **Moving variant:** legs walk (the gait is already a run-like step). `kMov = 0.6`: arms pump at 60 % of the `0.26` amplitude and swing with the gait phase, torso `theta = 0.12` added to the gait lean.

## Move: TStep

- **Name / origin:** T-step (Melbourne shuffle footwork, https://en.wikipedia.org/wiki/Melbourne_Shuffle): one foot slides out to the side and back while the other turns into a T.
- **Tempo range:** 120-160 BPM.
- **Counts:** Beat 1: rig-R foot slides out to the side. Beat 2: slides back in (forming the T against the planted foot). Beat 3-4: the same on the rig-L foot.
- **Joint spec:**
  - Pelvis: `py = -0.05 - 0.015*Dip(b)`; `px = 0.04*sinT(r)` (sway over the planted foot).
  - Torso: `theta = 0.05`; `psi = 0.10*sinT(r)`; `phi = 0`.
  - Head: `thN = 0.04*Dip(b)`.
  - Arms: fists sway side to side at the belt: `Wrist_s = O(0.12*sinT(r), -0.28, 0.20)` (unmirrored), hint `hLow`.
  - Legs: the active foot is rig-R for `c < 2` and rig-L for `c >= 2`; its excursion `e = sinT((c mod 2)/4)` (0 at the start of its two beats, 1 at beat 2's start, 0 again after two beats: out on the first beat, back on the second). Active foot: `ankle.x = s*(0.098 + 0.17*e)`, toe turned by `a = 1.2*e` rad (the foot rotates into the T), `ankle.z = -0.04*e`; passive foot: `ankle.x = s*0.098`, flat. LegFit.
- **Moving variant:** legs walk. `kMov = 0.6`: arm sway and bounce only.

## Move: Sprinkler

- **Name / origin:** The sprinkler (https://en.wikipedia.org/wiki/Sprinkler_(dance)): one hand behind the head, the other arm extended and swept in short jerks through an arc like a garden sprinkler; bent knees and a waist bend.
- **Tempo range:** 80-140 BPM.
- **Counts:** Beats 1-3: three jerks, each beat the extended arm steps a third of the way across the arc (beat 1 first, beat 3 last); beat 4: the arm sweeps back smoothly to the start.
- **Joint spec** (`psi` is the arc; the mesh cannot yaw the body so the arc is +/-0.9 rad of chest twist):
  - Pelvis: `py = -0.06 - 0.01*Dip(b)`; `px = 0`.
  - Torso: `theta = 0.25` (waist bend); `phi = 0`; `psi`: for `c < 3`: `-0.9 + 0.6*(floor(c) + Smooth(min(b/0.35, 1)))` (steps from -0.9 to +0.9), for `c >= 3`: `Lerp(0.9, -0.9, Smooth(b))`.
  - Head: `thN = 0`, `phH = 0`.
  - Arms: rig-R extended: `wrist = o(0.50, 0, 0.02)` (0.50 m, horizontal at shoulder height, hint `hOut`); rig-L behind the head: `wrist = o(-0.05, 0.16, -0.06)` (beside the back of the head, `|x| = 0.13`), hint `h(1,0.2,-0.3)`.
  - Legs: `Planted(0.14, 0.12)` (knees bent).
- **Moving variant:** legs walk. `kMov = 0.6` (`psi` +/-0.54), `theta = 0.15`, arms as above (blend 1).

## Move: ShoulderLean

- **Name / origin:** Shoulder lean, the classic hip-hop bounce with alternating shoulder drops (generic, no single source).
- **Tempo range:** 70-110 BPM.
- **Counts:** Lean toward rig-R on beats 1 and 3, toward rig-L on beats 2 and 4, with a bounce on each beat.
- **Joint spec:**
  - Pelvis: `px = -0.04*E`; `py = -0.05*Dip(b)`.
  - Torso: `phi = 0.18*E`; `theta = 0.06`; `psi = 0.05*D`; shoulders `sh_s = -0.02*s*E` (the leaning side drops a little extra).
  - Head: `phH = -0.10*E`; `thN = 0.08*Dip(b)`.
  - Arms: `wrist = o(0.12, -0.32, 0.18)` (fists low in front), hint `hLow`, with a swing `fwd += 0.05*s*D`.
  - Legs: `Planted(0.15, 0.03 + 0.04*Dip(b))`; free-foot heel lift `0.02*max(0, -s*E)`.
- **Moving variant:** legs walk. `kMov = 0.6`: `phi = 0.11*E`, arms blend 0.5.

## Move: Griddy

- **Name / origin:** The Griddy (NFL end-zone celebration made popular by Justin Jefferson; no authoritative source page found, the description is assembled from tutorial summaries and is a reconstruction, see the research notes): wide knees, feet stepping and crossing behind while the arms swing opposite, one arm bent up, one down.
- **Tempo range:** 100-140 BPM.
- **Counts:** One foot crosses behind per beat: rig-R foot on beats 1 and 3, rig-L foot on beats 2 and 4; the arms alternate up/down on the same beat.
- **Joint spec** (`Ph = sinT(c/2) = D`; `XR = max(0, D)` (rig-R foot crossing), `XL = max(0, -D)`):
  - Pelvis: `px = -0.05*D`; `py = -0.12 - 0.02*Dip(b)`.
  - Torso: `theta = 0.12`; `phi = 0.05*D`; `psi = 0.10*D`.
  - Head: `thN = 0.05`.
  - Arms: `u_s = (1 + s*D)/2` (rig-R is up while `D > 0`); `wrist_s = Lerp(o(0.06, -0.47, -0.08), o(0.10, 0.38, 0.05), Smooth(u_s))` (down-and-back straight arm to an elbow-out arm beside the head, 0.39 m), hint `h(1, 0, -0.5)` while up and `h(0.3, -1, -0.5)` while down.
  - Legs: `Planted(0.28, d)` with `d` from `py`; crossing foot `X_s` = `XR` for rig-R, `XL` for rig-L: `ankle.x = s*(0.28 - 0.26*X_s)`, `ankle.z = -0.12*X_s`, toes turned out `a = 0.3`; LegFit.
- **Moving variant:** legs walk. `kMov = 0.7`: arm alternation and torso roll stay, `px = -0.03*D`.

## Move: Moonwalk

- **Name / origin:** Moonwalk (https://en.wikipedia.org/wiki/Moonwalk_(dance); technique, slide the flat foot back on one ball-of-foot support and switch, from https://instructables.com/How-to-Moonwalk-4).
- **Tempo range:** 90-130 BPM.
- **Counts:** Standing only. One slide-and-switch per beat: rig-R foot slides back flat while rig-L is on the ball (beat 1), switch (beat 2), and so on; the body appears to travel forward.
- **Joint spec** (per foot `ph = frac(c/2 + (s == +1 ? 0 : 0.5))`; the body cannot glide on the ground, so the legs move in place):
  - Pelvis: `py = -0.03`; `px = 0`; `pz = -0.02*D` (a hint of drift).
  - Torso: `theta = -0.05` (leaning back against the glide); `phi = 0.03*D`; `psi = 0.06*D`.
  - Head: `thN = 0.04*Dip(b)`.
  - Arms: `wrist = o(0.10, -0.30 + 0.03*s*D, 0.12 + 0.08*s*D)` (hands held at the waist, swinging a little), hint `hLow`.
  - Legs: sliding phase (`ph < 0.5`): foot flat, `ankle.z = Lerp(0.10, -0.12, 2*ph)` (linear, it is a slide), `ankle.y = 0.085`; ball phase (`ph >= 0.5`): heel up `h = 0.06` and `ankle.z = Lerp(-0.12, 0.10, Smooth(2*(ph - 0.5)))` (drawn forward on the ball); feet at `x = s*0.10`; toe via the heel-up rule; LegFit.
- **Moving variant:** not possible while walking (legs keep the walking gait and sliding feet do not make sense); when `m > 0.3` fall back to the **`Bounce`** moving variant with `theta = -0.05` and the same arms.

## Move: Sway

- **Name / origin:** Slow sway with arms raised (the "lighters" crowd sway; generic, no single source).
- **Tempo range:** 60-100 BPM (one full left-right per bar).
- **Counts:** Over the whole bar: sway toward rig-R through beats 1-2 (peak on beat 2), back toward rig-L through beats 3-4 (peak on beat 4).
- **Joint spec** (`sw = sinT(r)`):
  - Pelvis: `px = 0.07*sw`; `py = -0.02`.
  - Torso: `phi = -0.06*sw` (S-curve: ribcage against the hips); `theta = 0`; `psi = 0.05*sw`.
  - Head: `phH = 0.10*sw`; `thN = -0.05`.
  - Arms (both hands up, swaying with the body): `wrist = o(0.06, 0.45, 0.04) + O(0.10*sinT(r - 0.05), 0.02*sinT(2*c), 0)` (0.455 m before the sway term; the `O` term shifts both hands the same way, so the wrist clamp at 0.50 m can bite at the extremes), hint `hUp`.
  - Legs: `Planted(0.14, 0.02)`; free-foot heel lift `ankle.y += 0.02*max(0, -s*sw)`.
- **Moving variant:** legs walk. `kMov = 0.6`: arms up and swaying at 0.6 of the sway, `px = 0.04*sw` (clamped).

## Move: FolkClap

- **Name / origin:** Folk clap and stomp (polka/folk dance clapping, generic, no single source).
- **Tempo range:** 80-140 BPM.
- **Counts:** A clap on every beat with an alternating foot stomp: rig-R stamps on beats 1 and 3, rig-L on 2 and 4 (the stamping foot lifts during the half-beat before).
- **Joint spec** (`cl = Dip(b)^3`, 1 on the beat):
  - Pelvis: `py = -0.03 - 0.02*Hit`, with `Hit = (1 - b)^3` (a small drop on the stamp); `px = 0.03*E`.
  - Torso: `theta = 0.05`; `phi = -0.03*E`; `psi = 0`.
  - Head: `thN = 0.04*Dip(b)`.
  - Arms: `wrist = o(0.20 - 0.35*cl, -0.05 - 0.05*cl, 0.30)`, hint `hLow` (a clap on each beat).
  - Legs: `Planted(0.13, 0.03)`; stamping foot `ankle.y += 0.07*max(0, -sinT(b))` (lifts in the second half of the preceding beat, lands on the next beat) for rig-R when `k` is odd (before beats 1 and 3) and rig-L when `k` is even (before 2 and 4).
- **Moving variant:** legs walk (no stamps). `kMov = 0.8`: claps stay at full amplitude, bounce `py` clamped.

## Move: HandsOnHipsSkip

- **Name / origin:** Hands on hips skip (a folk-dance skip, generic, no single source).
- **Tempo range:** 100-160 BPM.
- **Counts:** One skip per beat, alternating the lifted knee: rig-R knee on beats 1 and 3, rig-L on 2 and 4; hands stay on the hips.
- **Joint spec** (`Hop(b)` = 0 on the beat, 1 mid-beat; `alt_s = 1` if the lifted leg is `s`, i.e. rig-R for `k` even):
  - Pelvis: `py = 0.06*Hop(b) - 0.04` (hop up, land bent); `px = 0.02*D`.
  - Torso: `theta = 0.04`; `phi = 0.05*D`; `psi = 0.10*D` (shoulders turn toward the lifted knee).
  - Head: `thN = 0`; `phH = 0`.
  - Arms: `AKIMBO` both, with `up += 0.02*Hop(b)`.
  - Legs: standing leg `Planted(0.11, 0.04)` with `ankle.y += 0.04*Hop(b)` (off the ground mid-hop); lifted leg: `ankle.y += 0.14*Hop(b)`, `ankle.z += 0.10*Hop(b)`, toe down `liftFrac = Hop(b)`; LegFit.
- **Moving variant:** legs walk (no hops). `kMov = 0.5`: `AKIMBO` hands, torso roll and twist only, bounce `py` clamped.

## Implementation hints

- **Frame flow:** compute `c = 4r`, `b = frac(c)`, `k = floor(c)`; pick the move function by `(Style, Move mod MoveCount)`; it returns the channels (`P, t, theta, phi, psi, thN, phH, shL, shR`, wrist/elbow targets, ankle targets); blend with `we = Smooth(Weight)` against the rest rig (the gait rig when walking); build the spine chain; aim the arms (`Limb.Solve` for `o/O/H` targets, directly for `ArmChain`); clamp wrists to 0.50 m of the shoulder and push them out of the torso (clearance rule); solve the legs after `LegFit` when `m < 1`.
- **Bounce helper:** `py -= A*Dip(b)` (down on the beat) or `py = -d + A*Hop(b)` (airborne mid-beat), shared by `Bounce`, `Headbang`, `Floss`, `OrangeJustice`, `GangnamStyle`, `HandsOnHipsSkip`.
- **Twist chain:** `theta, phi, psi` with the weights 0.25 (Waist), 0.6 (Chest), 1.0 (Shoulders, Neck) in the Notation section; all torso moves (`Sprinkler`, `Robot`, `Carlton`, `FistPump`) only fill these three numbers.
- **Wrist sequencing by count:** `Seq`/`SeqHold` over `c` for keyframe moves (`DiscoPoint`, `Robot`, `Macarena`, `Dab`); the same `Smooth` transition time (0.12-0.45 beat) everywhere keeps moves snappy on the beat; a clap is `cl = ((1 - cosT(c/2))/2)^2` (backbeat) or `Dip(b)^3` (every beat).
- **Alternation:** `D = sinT(c/2)` and `E = cosT(c/2)` give left/right alternation with a two-beat period; `Sq(x)` turns them into a planted weight-shift flag; `sinT(r)` gives a one-bar sway.
- **Leg solver order:** pelvis drop -> ankle targets -> `LegFit` -> `Limb.Solve`; toes from the foot rules in the Notation section; never let a foot leave the ground in the same frame the hips hop unless the move lifts it explicitly.
- **Blend-in:** `Weight` ramps every channel from the rest pose; apply `we` to ankle targets too, so a dance that starts while standing doesn't pop the feet.
- **Safe ranges when tuning:** wrist 0.50 m from the shoulder, hand at least 0.21 m from the `Hip..Neck` axis, `|theta| <= 0.87`, `|psi| <= 0.9`, `|phi| <= 0.2`, head nod `-0.5..0.6`, pelvis `|px|,|pz| <= 0.2` standing (`0.04` when moving), hip height at least 0.66 (deepest twerk).

## Research notes (what was sourced and what was not)

- Sourced from pages read: Twerking, Dab, Floss, Orange Justice, Sprinkler, Running man, Robot, Headbanging, Air guitar, Melbourne Shuffle, Fist pump, Gangnam Style, Moonwalk, Macarena (song), the Carlton tutorial (steezy.co) and the Macarena/Griddy/Fortnite/moonwalk tutorial summaries returned by web search. Wikipedia pages on the dances are thin on joint-level detail, so joint numbers are authored against the bone lengths above, not measured.
- **Not sourced beyond general knowledge:** the DiscoPoint pose (the Saturday Night Fever article does not describe it), ArmWave (Wikipedia page unreachable), the exact Macarena hand-to-hip and turn phase (omitted on purpose: no body yaw), the Griddy (only SEO-style tutorials found: counts and arm positions reconstructed), air-guitar windmill detail, and all generic base moves (SideStepClap, HipSway, ClapBackbeat, Bounce, ShoulderLean, Sway, FolkClap, HandsOnHipsSkip).
- Not implementable in this rig: body turns (Macarena quarter turn, sprinkler's full sweep), floor moves (the worm), palm orientation, head yaw.
