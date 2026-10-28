# To be tested by a human

What the headless checks could not verify: hands-on clicks, a real pad, a headset, a real-size region, a look or feel to judge.
Each PR that leaves something here adds it in the same change (`docs/notes/general/human-test-list.md`).
Once tested, delete the line, or note what was found (an issue link) and delete it once fixed.
Something to retest over and over belongs in the playtest suite instead (`tools/playtest.sh`, `general/playtest`).

Format: `- [ ] what to do → what should happen` under the feature's heading, with its PR.

## What E acts on is outlined ([#1291](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1291))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499896.6,1117463.7` (Cinémas du Grütli, Geneva), then walk in.

- [ ] At the booth's control panel, stand at its left half, then its right → the outline covers only that half, and the floating prompt says what it does (lights / screen). E does what the prompt says.
- [ ] Indoors: a cupboard, a fridge, a locker, a shop counter, a vending machine, a bank counter → each outlined alone (not the whole room), and the outline moves when you turn to the next one.
- [ ] A flat's front door, the street door from inside, an elevator's call button and its cabin panel → the leaf or the button plate is outlined; E works that one.
- [ ] Outside: a building's door (plain, a barn's pair, a garage's roll-up door), a building site's skip and crane ladder, a farm stand, a summit cross or stamp post, a reward chest → outlined while the prompt shows.
- [ ] Look: is the border readable over a dark cupboard, at night, through the Cartoon, PS1 and Realistic styles? Is a whole ski lift shed or another player outlined too much?
- [ ] Pad (Y) and VR (grip near the thing) → the same thing outlined as with the keyboard.
- [ ] A dropped item on the floor next to a cupboard: point at the item → only the item is outlined and the floating prompt is gone (the bar says Pick up).

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

## Public toilets by gender ([#1288](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1288))

`--chunks D:/UnitSportSwitzerland/terrain_chunks`; CinéVersoix `--at 2501700,1126110`, BowlinGland `--at 2510395,1140935`.

- [ ] In a multiplex or a big bowling centre's lobby, find the toilets at its end → a women's and a men's door with their signs over them, and the accessible WC's door. Open one → the room with the basins and mirrors beside the door, the stalls along its far wall.
- [ ] Walk in → one door to the room, then a door to each stall, nothing between; in the men's, urinals with screens along the lobby wall. Walking feels right: no snagging on the screens, the dryer, the doorways (0.9 m).
- [ ] E at a stall door → it swings into the stall and shuts again; step in and turn round with the door open → room to stand (the stall is 0.88 m clear). Pad: Y. VR: grip the door.
- [ ] Two clients: one opens a stall door → the other sees it open.
- [ ] A small town cinema, a café, a bar → a shared anteroom with a women's and a men's stall, or one WC with its own door and a sign. A village Beiz in a house → the house's WC has the sign and a door leaf.
- [ ] The light in the stalls (they have no lamp of their own; the washroom's reaches them) → not darker than the washroom.
- [ ] An IKEA → toilets in a low block along the back wall of the market hall, the stock room beside them.

## Geneva lag, doors, sound occlusion setting ([#1282](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1282))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500300,1118450` (Geneva).

- [ ] Spawn in Geneva, F4 or the perf overlay → steady frames near the GPU's time (measured 20 ms median, was 52 ms).
- [ ] Walk (no slide) into a house's open door, with the Adventure layer on → you go through, no climb up the facade. Push into a plain wall for a second → the climb still grabs it.
- [ ] Walk past a café or bar, then into one → its music fades in as you near it (it wakes within ~15 m of its reach) and plays inside.
- [ ] Settings › Audio › **Sound occlusion** off, stand behind a building from a playing car radio → heard as in the open; on → muffled again. Same row on a pad and in the VR panel.

## Cinemas: hall height, a panel per hall, aisles to the walls, the service door, film music ([#1280](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1280))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499896.6,1117463.7 --detail high` (outside Cinémas du Grütli, Geneva).

- [ ] Go in: the halls are three storeys high (8 m), not one.
- [ ] The booth (up the stair in the staff corridor): walk its whole length → nothing blocks the way, and every hall's panel beside its projector can be reached.
- [ ] At a hall's panel, E on its right half → only that hall's screen comes on; another hall's panel works its own. Its left half cycles that hall's lights. Same with the pad's interact and a VR grip.
- [ ] Online, a second player in another hall → sees each hall's screen as set; a late joiner too.
- [ ] In a hall: the side aisles run up against the side walls, the walkway along the back wall behind the top row, no drop anywhere.
- [ ] At the top of one aisle: walk up the steps to the service door, E opens it → walk through into the booth (onto its deck in a floor-level booth), and back down into the hall.
- [ ] A hall's screen on → its film music plays (Radio Swiss Classic; offline, the Film modules); off → it stops. Judge the five Film modules: drop any that do not fit.
- [ ] Grütli's third hall (and Empire's 3rd, 5th, 6th) is mid-film when you arrive: its door shut, lights off, the cat on, music playing. The others lit and dark; their doors shut too and open with E.
- [ ] Two halls showing side by side → different music in each (some halls play the radio, others the modules).
- [ ] Walk along a row's lane to a seat → no climbing through seats, the step down to the next row at the lane's front edge; E sits facing the screen, E or walking stands up. Rows staggered, the top row against the back wall.
- [ ] Switch a hall's screen off and on → another tune.
- [ ] Sit down → your seat's cushion folds down under you (and someone else's, online); stand up → it folds up again.
- [ ] Lights off in a hall → they fade over some seconds to dark but for the screen's glow; dim → between. Walk into another hall → its level at once.
- [ ] Look at a hall's port from the seats with the screen on → the picture faint in the hole, the lens glowing in the picture's colour; screen off → both gone.
- [ ] The lobby's music: the same level across the room.
- [ ] The beam: bright at the lens, nearly gone at the screen, faint from the side; walk into it → a dark wedge behind you in the beam, not only on the screen.
- [ ] Grütli from outside → one entrance, no extra doors round the building.
- [ ] The box office's glass → see-through, the cashier visible behind it.
- [ ] High detail, in a hall with its screen on, walk into the beam → the cat on you, your shadow on the screen.

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
- [ ] Pad: hold Y at a party member → the same invite; Y with no target lets go. VR: the same through the pad route (hold Y); gripping a hand and opening the grip are not built. Judge whether the hand pose looks natural.
- [ ] Rope up on a glacier or a snowfield (`/rope <player>`), then fall off a ledge or into the snow → the rope catches you, you hang at the rope's length; the partner holds E 2 s (pad hold Y) → you rise. Judge the swing and the haul speed.
- [ ] A real glacier unroped, walking across it (`Settings › Gameplay › Crevasses` on) → now and then a hidden crevasse's bridge breaks: you stumble back winded and a blue slot opens. Roped to a standing partner → a stumble only. Judge how often it happens (4 % of 12 m cells) and whether it is fun or an annoyance. Not built: the slot with its own collision, you cannot fall in.
- [ ] A mountain hut, a Beiz or a lit campfire with a fondue pot (E, or use the pot item at the fire) → the panel opens, the party sits down; trace an eight with the mouse (hold LMB) and the right stick (VR: the stick; no real fork yet) → the cheese stays smooth; stop stirring on a high heat → it splits with a hiss. Judge the stirring feel and the panel's size on a pad and in VR.
- [ ] Lose your bread (keep dipping) → the forfeit popup (line, buy a round, yodel) answers with 1-3, D-pad + A and the laser; `--forfeits mild` hides the money one.
- [ ] Two-handle gate at a castle, hut or mine (`RewardPlacer` layouts, real region) → two players hold E at the two wheels within 1.5 s and it opens for 60 s; leg-up: crouch at a high boulder, the partner jumps onto the step → 2.6 m climb-jump without stamina.
- [ ] With a veteran (3 summits) guiding a newcomer to a first summit on a real map → both get their cap (Erstbesteigung, Bergführer); the Journal's Collection > Together page shows the stamps, the week's minutes and the mentor table. Judge the page's layout.
- [ ] `--world fixture` is enough for everything except the glacier, the real huts and the gates; the Crevasse, Caquelon, Rope, Harness and Gate models are in the model viewer (`--models`, category Adventure) → look right in Cartoon, PS1 and Realistic.
## Festival framework, part 1 of the festivals ([#1295](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1295))

Nothing is player-facing yet but the Journal tab and the poles: the check-only festival stands in for the real ones. Offline: `<godot> --path . -- --festival test --date 2027-03-11 --systems ui,physics,occasions,adventure --world fixture` (add `--chunks D:/UnitSportSwitzerland/terrain_chunks` for the real map, where the test festival is held at the Riddes spawn).

- [ ] Press J → the **Festival** tab (between Collection and Travel) lists "Test festival at the test field", day 2 of 5, the bell open, the gong tomorrow, the closing show, the score bar and the Batzen. Judge the layout at 1080p and on a phone-sized window (`--mobile`): readable, nothing cut off but the scroll.
- [ ] Pad: the tab opens with LB / RB; the stick or D-pad scrolls the page (it has no buttons yet) → it scrolls, focus stays visible.
- [ ] Walk round the spawn → a ring of 16 red Swiss-flag poles stands about 60 m out. Settings › Occasions › the test festival › Off → the poles go, while the Journal's Festival tab and the score stay.
- [ ] Real map, dev region, a dedicated server with the same flags → the log says `[festival] running: test day 2/5 at the test field`; join with a client and open the Journal → the same tab, with the same day.
- [ ] Real map: a real festival site outside the region relocates to the largest town ("held this year in Bulle"): needs a real festival, so this waits for part 3 (Älplerfest); the rule is unit-tested only.

## Festival fireworks and the closing show ([#1310](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1310))

`--festival test --date 2027-03-14 --time 22 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (the last day of the check-only festival, 22:00): a show starts about 15 s after load. Online: a server with `--festival test` plus a client, same date.

- [ ] Walk to the field (the mortar rack with its tubes stands at its middle, the flag poles round it) → the first shell climbs with a spark trail, bursts with a flash on the ground, and its bang comes a moment later. Is the delay believable, the bang low and round rather than a click?
- [ ] Watch from 150 m, 1 km and 3 km (walk or `/tp`) → the bursts stay readable sparks at 3 km, the bang is 9 s late there, and the light on the ground and the tent fades with distance.
- [ ] Peony, chrysanthemum, willow, palm, ring, crossette (four small ones a second after) and the Swiss cross (a white cross hanging over a red burst, turned to face you) → each is told apart from the others. Judge the willow's droop and the palm: do they read as such?
- [ ] The finale (the last 20 s of 6-12 minutes; wait, or join late) → forty shells with no stutter. Note the frame time with F3 or `--perf`: the plan's budget is under 0.3 ms.
- [ ] Join a second client halfway through the show → it sees the shells from where the show is, not a pile of old ones, and the same shell at the same moment as the first client.
- [ ] Cartoon, PS1 and Realistic (Settings › Style) → the sparks, flashes and sky look right in each.
- [ ] Settings › Occasions: set the test festival to Off → no shells and no bangs, and the Journal's Festival tab still shows the score.
- [ ] VR headset: stand at the field, look up → depth reads right; no particle sheet or flash light clips close to the eyes.
- [ ] The player at the site for 20 s of the show → the server log says `watched the closing show of test` once.
- [ ] Real map: a real festival site is not part of this PR (the Älplerfest comes with its games); the real launch line (a lake's shore) is not built.

## Adventure errands: givers, dialogue, daily Postkarten, canton reputation ([#1311](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1311))

On the fixture (`--world fixture --pois fixture --givers fixture --systems ui,terrain,physics,adventure`) the eight givers stand within 350 m of the start; on a real map there are wardens at the huts, masters at the stations and the landings only (the OSM stage for post offices, farms and dairies is not built).

- [ ] Walk up to a giver → a figure stands at its place with an amber card glyph over its head; within 3 m the prompt says "Talk to ..." and its outline shows. Judge the figure's height, the glyph's size and whether it reads from 20 m.
- [ ] E (pad Y, VR: reach out and grip toward the giver) → the dialogue opens bottom centre with three choices. Pad: D-pad + A pick, B leaves, LB / RB switch cards. VR: laser + trigger on the panel. Judge the panel's size and distance in VR.
- [ ] Take the mail run → three sacks in the pack, the card in the Journal's Quests tab and the tracker, a stamp toast and sound. Hand a sack over at each stop (Journal › Hand over) → CHF in the account, +3 reputation in the canton.
- [ ] Take three cards, then ask a fourth → "Three cards a day". Wait for midnight in Switzerland (or `--errand-day-offset 2`) → the running cards expire, their cargo goes back, three new cards.
- [ ] With a friend in a party: both take the same card (or Share with party in the Errands tab, the friend Joins) and finish it → both get "Together with ...: +CHF" and +1 reputation. Judge whether +50 % is felt.
- [ ] Journal › Errands tab on a pad (LB / RB to it): cards, Track, Share, reputation. Judge the layout at 1080p and with `--mobile`.
- [ ] Reputation 10 / 30 / 60 / 100 points in one canton (many cards) → level-up toasts, the title "Ehrenbürger von ..." at the top. Judge the pace.
- [ ] Giver models in the model viewer (`--models`, People › Errand givers) → eight figures with the Sennenkäppi, guide hat and vest; look right in Cartoon, PS1 and Realistic.
