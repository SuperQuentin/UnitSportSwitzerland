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

## Cinemas: hall height, a panel per hall, aisles to the walls, the service door, film music ([#1280](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1280))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499896.6,1117463.7 --detail high` (outside Cinémas du Grütli, Geneva).

- [ ] Go in: the halls are three storeys high (8 m), not one.
- [ ] The booth (up the stair in the staff corridor): walk its whole length → nothing blocks the way, and every hall's panel beside its projector can be reached.
- [ ] At a hall's panel, E on its right half → only that hall's screen comes on; another hall's panel works its own. Its left half cycles that hall's lights. Same with the pad's interact and a VR grip.
- [ ] Online, a second player in another hall → sees each hall's screen as set; a late joiner too.
- [ ] In a hall: the side aisles run up against the side walls, the walkway along the back wall behind the top row, no drop anywhere.
- [ ] At the top of one aisle: walk up the steps to the service door, E opens it → walk through into the booth (onto its deck in a floor-level booth), and back down into the hall.
- [ ] A hall's screen on → its film music plays (Radio Swiss Classic; offline, the Film modules); off → it stops. Judge the five Film modules: drop any that do not fit.
- [ ] The box office's glass → see-through, the cashier visible behind it.
- [ ] High detail, in a hall with its screen on, walk into the beam → the cat on you, your shadow on the screen.
