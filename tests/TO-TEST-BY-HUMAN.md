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

## Interiors follow the building's outline ([#1294](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1294))

Generated world (the spawn's villages have an L, a U, a courtyard, a trapezoid and a bent block each within a few km: `--shapedcheck` prints where), or a real old town (Geneva above, Sion `--at 2594000,1120000`).

- [ ] Walk into a building with an angled facade → the rooms along it have one straight angled wall with windows, not a staircase, and nothing pokes through it. Walk along it: no snag, no gap to fall through at the corners.
- [ ] Walk into an L-shaped house → rooms in both arms (a hall down the far arm with rooms off it), none out in the yard.
- [ ] Look out of a window on an angled wall → the day's light comes in through it like any other.
- [ ] VR: walk into an angled room → the walls hold you (teleport into the corner too).
- [ ] A house drawn on the web with an angled wall (`--house-layouts`, see `net/house-layouts`) → that wall is one straight wall in the game, its door in it.

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
