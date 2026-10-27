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

## Stuck keys after a hitch ([#1284](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1284))

Windowed on Windows, with a heavy place to cause hitches (Geneva: `--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500300,1118450`).

- [ ] On foot, hold Shift to sprint, let go of it during a hitch (a teleport or `/goto` far away freezes the game for a moment) → the log says `[input] Shift was let go during a hitch: released` and the player walks again within ~0.2 s, without pressing Shift again.
- [ ] Same with W held → the player stops.
