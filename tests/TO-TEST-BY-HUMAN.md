# To be tested by a human

What the headless checks could not verify: hands-on clicks, a real pad, a headset, a real-size region, a look or feel to judge.
Each PR that leaves something here adds it in the same change (`docs/notes/general/human-test-list.md`).
Once tested, delete the line, or note what was found (an issue link) and delete it once fixed.
Something to retest over and over belongs in the playtest suite instead (`tools/playtest.sh`, `general/playtest`).

Format: `- [ ] what to do → what should happen` under the feature's heading, with its PR.

## /locate and chat coordinates ([#1278](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1278))

Offline is enough for most: `--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2538000,1152500` (Lausanne), then `/locate castle`.

- [ ] Click a pin or coordinates in an answer → the choices list opens beside it. Esc closes the list, and the chat stays open.
- [ ] Choices › **Teleport** → you land at the point. Online, on a server run with `--no-teleport` and not as an admin, it is refused with a reason, and Teleport is not in the list at all.
- [ ] Choices › **Track** → navigation's marker, compass and guiding wind point there. Pick Track on the same point again → tracking stops.
- [ ] Choices › **Show on map** → the adventure map opens centred on a red pin with the place's name.
- [ ] Choices › **Copy**, then paste into the chat and send → the sent line shows the coordinates with a pin, and its own choices work.
- [ ] Pad: hold Back › **Chat** → the chat opens on the "/" picker. Build `/locate church + spire` with the D-pad and A only, then Send. Move up to an answer line, press A → its choices open. B closes the choices, then the chat.
- [ ] VR: Menu hold › Chat → the picker and a point's choices can be pressed with the ray.
- [ ] Judge the size: the picker and choices buttons are 30 px tall (the usual `UiKit.Button` is 38 px). Comfortable on a pad and a 4K screen?
- [ ] Dev region (6,699 tiles), first server boot with `user://cache/locate/` deleted → the `[locate] building index: … in N ms` line. Estimated ~50 s: note the real time here. During the build, `/locate apartment building` from a player should answer at once round them, with the "isn't finished" note only when something unread is nearer.
- [ ] A region with `pois.bin` → `/locate a lake`, `/locate station`, `/locate church` (named after the survey's church) answer with names.

## Geneva lag, doors, sound occlusion setting ([#1282](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1282))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500300,1118450` (Geneva).

- [ ] Spawn in Geneva, F4 or the perf overlay → steady frames near the GPU's time (measured 20 ms median, was 52 ms).
- [ ] Walk (no slide) into a house's open door, with the Adventure layer on → you go through, no climb up the facade. Push into a plain wall for a second → the climb still grabs it.
- [ ] Walk past a café or bar, then into one → its music fades in as you near it (it wakes within ~15 m of its reach) and plays inside.
- [ ] Settings › Audio › **Sound occlusion** off, stand behind a building from a playing car radio → heard as in the open; on → muffled again. Same row on a pad and in the VR panel.

## Stuck keys after a hitch ([#1284](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1284))

Windowed on Windows, with a heavy place to cause hitches (Geneva: `--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500300,1118450`).

- [ ] On foot, hold Shift to sprint, let go of it during a hitch (a teleport or `/goto` far away freezes the game for a moment) → the log says `[input] Shift was let go during a hitch: released` and the player walks again within ~0.2 s, without pressing Shift again.
- [ ] Same with W held → the player stops.

## Versteckis, hide-and-seek in a village ([#1273](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1273))

Host a game: main menu › Host, then Activities › **Versteckis** › Open (bots fill the empty places).

- [ ] Real map, dev region: open a game near a village with a church → the play area rings the church square, the decor props stand in sensible places (none inside houses or in the road), and the hiders find spots.
- [ ] As a hider: open the Disguise radial, pick the hay bale, stand still among the bales → you can't tell yourself apart from the decor from 15 m (look with a friend or a spectator).
- [ ] As the seeker: wait out the blind count, then Tag and Sniff → a miss costs the cooldown, a catch shows in the results table.
- [ ] Pad: the radial, Tag, Sniff, Decoy, Sprint and Moo on the hotbar; hold Y at the church bell → it rings and your disguise drops.
- [ ] VR: hold the grip near the bell or the Höhenfeuer → the objective runs; the hotbar tools work from the wrist.
- [ ] Phone or touch: the Versteckis tab and the hotbar tools are usable.
- [ ] Windowed `tools/brcheck.sh` → the Battle Royale still looks and plays as before (its spectator camera now lives in `Ui/FollowCam`).
- [ ] A game with 8 players (or 1 + 7 bots) → frames stay steady (F4), the server's `--serverstats` line shows no spike.

## Adventure together: hands, rope, the shared pot, mentors ([#1274](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1274))

Two players in one party, `--systems ...,adventure`. On a real region for the glacier and the climb lines.

- [ ] Hold E 0.5 s at a party member, the other accepts with F2 → hands are posed to meet at hip height on both screens; walk with no input as the follower and you are led along the leader's trail. Tap E at the same person → still the fist-fight challenge.
- [ ] Pad: hold Y at a party member → the same invite; Y with no target lets go. VR: grip a party member's hand within 15 cm → a mutual link; open the grip → let go. Judge whether the hand pose looks natural in both.
- [ ] Rope up on a glacier or a snowfield (`/rope <player>`), then fall off a ledge or into the snow → the rope catches you, you hang at the rope's length; the partner holds E 2 s (pad hold Y, VR hand over hand) → you rise. Judge the swing and the haul speed.
- [ ] A real glacier unroped, walking across it (`Settings › Gameplay › Crevasses` on) → now and then a hidden crevasse's bridge breaks: you stumble back winded and a blue slot opens. Roped to a standing partner → a stumble only. Judge how often it happens (4 % of 12 m cells) and whether it is fun or an annoyance. Not built: the slot with its own collision, you cannot fall in.
- [ ] A mountain hut, a Beiz or a lit campfire with a fondue pot (E, or use the pot item at the fire) → the panel opens, the party sits down; trace an eight with the mouse (hold LMB), the right stick, and in VR with the right hand → the cheese stays smooth; stop stirring on a high heat → it splits with a hiss. Judge the stirring feel and the panel's size on a pad and in VR.
- [ ] Lose your bread (keep dipping) → the forfeit popup (line, buy a round, yodel) answers with 1-3, D-pad + A and the laser; `--forfeits mild` hides the money one.
- [ ] Two-handle gate at a castle, hut or mine (`RewardPlacer` layouts, real region) → two players hold E at the two wheels within 1.5 s and it opens for 60 s; leg-up: crouch at a high boulder, the partner jumps onto the step → 2.6 m climb-jump without stamina.
- [ ] With a veteran (3 summits) guiding a newcomer to a first summit on a real map → both get their cap (Erstbesteigung, Bergführer); the Journal's Collection > Together page shows the stamps, the week's minutes and the mentor table. Judge the page's layout.
- [ ] `--world fixture` is enough for everything except the glacier, the real huts and the gates; the Crevasse, Caquelon, Rope, Harness and Gate models are in the model viewer (`--models`, category Adventure) → look right in Cartoon, PS1 and Realistic.
