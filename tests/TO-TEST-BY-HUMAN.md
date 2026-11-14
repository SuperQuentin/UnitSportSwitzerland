# To be tested by a human

What the headless checks could not verify: hands-on clicks, a real pad, a headset, a real-size region, a look or feel to judge.
Each PR that leaves something here adds it in the same change (`docs/notes/general/human-test-list.md`).
Once tested, delete the line, or note what was found (an issue link) and delete it once fixed.
Something to retest over and over belongs in the playtest suite instead (`tools/playtest.sh`, `general/playtest`).

Format: `- [ ] what to do → what should happen` under the feature's heading, with its PR.


## The far view's baked features and the far band ([#1657](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1657))

In a window, Cartoon, `--frame earth --date 2027-07-15 --time 12`, over Geneva.

- [ ] Settings → Graphics → Detail: Very low, then Low, then Medium, at 800 m looking across the town → Very low: the near leaves real, the rest the baked skin (note whether its roofs read too pink); Low: real far leaves out to about half the distance; no leaf flickers in or out while you hover.
- [ ] On a PC that struggles (or a headset), fly low over Geneva for a minute → if the frame rate drops, the furnished band shrinks (`[globe] far band at ...` in the log) and grows back when it recovers, without visible popping.

## The globe streamed from the server, a small base, no pops ([#1846](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1846))

In a window, Cartoon, `--frame earth --date 2027-07-15 --time 12`, the spectator (Shift+T).

- [ ] Offline, `--globe res://assets/globe` (the 1.5 MB base alone), climb from Lavaux to orbit and fly over Asia → the Earth from orbit looks as before; tile edges at 60-250 km as before (no worse).
- [ ] Join a server that has the pyramid (`--globe <dir>` on the server) from a copy with none and an empty cache, then climb slowly from the ground to 500 km over Switzerland → the globe sharpens as tiles arrive, no tile jumps in height when it splits or merges; note how long it takes to catch up after a fast climb (the check measured it lagging at 7 fps).
- [ ] With the pyramid on this PC (D:), climb and dive between 2 km and 300 km at full speed, watching the mountains on the horizon → no ground popping between levels (each level eases into the next).

## To the Moon and onto it (PR [#1893](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1893), [#1891](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1891), [#1850](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1850))

In a window, `--frame earth --sun real --date 2027-07-24 --time 10 --occasion none --bodies E:/UnitSportSwitzerland/planets`, Cartoon, on the spectator camera (Shift+T).

- [ ] Fly up and on toward the Moon at the wheel's top (about a minute) → the speed grows with the distance and falls again near the Moon; the picture never jumps at the handover (~66,000 km out), and the Moon's globe takes over from its sky disc in the same place.
- [ ] Come down to a few hundred metres and press T → you stand on the ground (no fall from the camera's height, no wait). Walk and sprint for a few hundred metres → the ground keeps coming under you, with craters down to a few metres, and no seam or step where the near ground meets the farther one.
- [ ] Jump → about 5.4 m up and over 5 s in the air (0.9 m on the Earth). Do the same with the pad (its jump and step-in buttons) and in VR → the same.
- [ ] From the Earth, fly toward Mars at the wheel's top (PR [#1899](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1899): about 15 min at light speed) → the speed reads in c past the Moon; nothing jumps leaving the Earth's sphere (~1 million km) or entering Mars'; Mars' globe takes over from its sky dot. Step in → you stand on Mars and a jump reaches ~2.4 m.
- [ ] In the spectator, tap ] (pad: D-pad →, VR: right stick down) a few times, then hold it → the ring steps Moon, Mars, the Sun..., an arrow at the edge when the body is off screen, and the hold turns the view onto it; fly toward the ring and it grows into the body. In VR, check the cue is readable on the HUD.
- [ ] Fly to Saturn and into its B ring, slowly (Wheel down) → over it a flat ring surface, then a haze with ice chunks round you (most small, a few house-sized), denser in the B ring than in the Cassini division; past the outer edge, level with the ring, a thin band. In a craft or on foot with gravity off, bump a chunk → it is solid. In VR, check the haze is not nauseating.
- [ ] Fly to Phobos (target cue: after Mars) and land with T → a potato-shaped moon with Stickney's big crater at one end; the walker drifts down for ~17 s; a jump climbs for minutes; walking bounces off every bump. Same on Deimos (smoother, 25 s to settle). In VR, check the slow float is not nauseating.
- [ ] In the shuttle, `/orbit 400`, tap ] (pad: D-pad →, VR: right stick →) → `TARGET Moon` on the HUD and a ring on the Moon; hold it → the nose swings calmly onto the Moon with the wings level; Z for the drive, full levers, keep holding → about 4 minutes to the Moon, slowing down by itself; Z when the HUD's height reads ~100 km → in lunar orbit. In VR, check the swing and the arrival are comfortable.
- [ ] From a lunar orbit in the shuttle, nose down and a slow drive to 8 km, then the levers: the HUD reads BELLY JETS; bring it down on its gear (G) at under 3 m/s with the stick holding the slide; step out, walk, get back in at the hatch (left side), and lift off on the jets → into Space at 10 km. On a pad and in VR too.
- [ ] In the UFO, `/orbit 400`, Z (pad D-pad ↑, VR right stick ↑): `DRIVE ON`; Space / Ctrl set its lever, it goes where you look; tap ] for the Moon and hold it: the way turns onto the Moon; slowing by itself, Z again near 100 km: a lunar orbit; then down past 8 km the hover brings it onto its legs.
- [ ] Type `/goto moon 26.13 3.63` (on foot or spectating) → the fly camera 30 km over the Apollo 15 site; fly down and T stands you there. Then `/goto earth 46.95 7.45 3` → back over Bern; `/goto pluto 0 0` → an error listing the bodies.
- [ ] On the Moon (or Mercury) on foot: boulders round the fresh craters near you, mostly small, a few a metre or two; walk into one → it stops you; jump onto a big one.
- [ ] Fly the spectator down into Jupiter (and Saturn, Uranus, Neptune) → a blue-grey sky over the white cloud deck, then a bright fog inside the clouds, darker and darker below; it stops 500 km under the clouds; T there says there is no ground. In VR, check the fog reads and is not nauseating.
- [ ] On Mars (PR [#1907](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1907)), stand through a morning and an evening (`/time speed` up) → a butterscotch sky by day, a blue glow round the sun as it sets, black with stars at night; from orbit a thin tan haze on the limb. Note anything that reads wrong in VR.

## Moon colonists ([#1945](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1945))

In a window, `--frame earth --sun real --date 2027-07-15 --time 10 --occasion none --bodies E:/UnitSportSwitzerland/planets`.

- [ ] `--moonsites artemis:close`, step in and walk the colony for ten minutes → colonists in their suits at the consoles and on the cans' aisles, now and then one walking through a tube to another room; nobody pops in or out where you are looking; nobody walks through a wall or into furniture.
- [ ] Outside by the solar field or the digger, wait a while → a colonist in a helmet appears there only while you are not looking that way.
- [ ] Note the frame time with a full crew in view (should stay as without them).

## The empty colonies: Marius Hills station and Plaskett station ([#1944](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1944))

In a window, `--frame earth --sun real --date 2027-07-15 --time 10 --occasion none --bodies E:/UnitSportSwitzerland/planets`.

- [ ] `--moonsites lavatube`, step in (T) at the rim, walk out onto the tower's bridge and down the stairs to the pit's floor → the flights take you down without a stumble, the rails hold you; the pit's wall looks like a wall (no gap where the ground squares meet).
- [ ] In the station, read the five terminals in order (E, pad Y, VR grip) → each page fits its screen and reads in the headset; the logs feel unsettling, not explained. Note whether the text is too small on a monitor.
- [ ] Walk the modules → dark, only the red lights stuttering; the helmet never comes off; the dust and drifts do not block the doorways.
- [ ] `--moonsites coldwar:close`, walk in through an airlock and along the spine into the biodome → the doorway lets you in, the trunks are solid, you cannot walk out through the glass; the radar turns outside.
- [ ] Judge the mood of both against the reference you gave (mood only, nothing copied).

## Moon rovers, the hangar door, landers and the runway ([#1943](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1943))

In a window, `--frame earth --sun real --date 2027-07-15 --time 10 --occasion none --bodies E:/UnitSportSwitzerland/planets`.

- [ ] `--moonsites artemis:close`, step in (T), walk to the two open rovers by the road past the hangar, E at one → you drive it; it floats over bumps on the Moon's gravity, tops out near 20 km/h, brakes long. Same with the pad and in VR (the wheel or the hand controller).
- [ ] Walk into the hangar (through the tube), get into the pressurised rover (E) → the helmet comes off in its cabin; drive at the rolling door → the inner door shuts, a hiss, the rolling door rolls up, and you drive down the ramp onto the plateau without a bump.
- [ ] On foot outside the hangar's rolling door, E (pad Y, VR grip) → "Open the hangar: out" opens it; inside, E at the inner door → "Cycle the hangar: in" shuts the rolling door and opens the inner one.
- [ ] `--moonsites runway`: the 3 km runway lies on the ground with no step at its edges (its strip eases out over 90 m); edge lights read at night.
- [ ] Land the shuttle on a free pad or the runway once the planets session's landing reads `MoonPads`.

## Moon Village, ILRS and the industry ([#1942](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1942))

In a window, `--frame earth --sun real --date 2027-07-15 --time 10 --occasion none --bodies E:/UnitSportSwitzerland/planets`.

- [ ] `--moonsites moonvillage:close`, step in (T), go through an airlock and along the glazed alleys between the stacks → no bumping, no invisible wall at either end, the helmet stays off inside an alley; the sky shows through the glass roof.
- [ ] By a solar array field (`--moonsites artemis:solararray`), speed time (`/time speed`) → the wings follow the sun smoothly, never snapping; at Artemis Base they stand near vertical.
- [ ] Watch the digger (`--moonsites moonvillage:digger`) for a minute → it crawls round its dark pit, its wheel turning; walking into it pushes you, never through it.
- [ ] `--moonsites icemine:close`: in Shackleton's dark the four floods light the mine and the ground round it; judge whether it reads (too bright, too dim, the pool's edge).
- [ ] `--moonsites massdriver:close`, wait a minute → the bucket sits on the cradle, then shoots along the rail and is gone; `--moonsites radiotelescope` for ten minutes → all dishes slew together to a new point.

## Into Artemis Base through an airlock ([#1941](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1941))

- [ ] At Artemis Base (`--moonsites artemis:close`), step in (T) outside: you wear the silver suit and the gold-visored helmet. Walk to the end of a spine, up the steps to the airlock's outer door, press E → the inner door (if open) slides shut, the lamp goes amber, a hiss, then the outer door slides open. Walk in, E again → the reverse; the helmet comes off once the outer door is shut.
- [ ] With the pad: the same with Y; the prompt reads "Cycle the airlock: in/out".
- [ ] In VR: reach out at a shut airlock door and grip → it cycles. Check the helmet does not block your view (first person draws no head).
- [ ] Walk the whole colony inside: the tubes (no bumping your head), the hub's control room, the cans' rooms (bunks, galley, lab...), a moondust dome's greenhouse, the hangar down its steps. Nothing to fall through, no invisible wall in a doorway.

## Artemis Base ([#1940](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1940))

- [ ] Go to Artemis Base (89.384° S, 150.055° W; `--moonsites artemis:close` drops the camera among its modules), step in and walk round the hub and along a spine → you bump into the cans, tubes and domes (they are solid, not yet walk-in: that is phase 3); the plateau is flat underfoot and eases into the natural ground past the pads with no step.
- [ ] Walk the road out of the hangar to the pads → three numbered pads with red edge lights, the road's dashes along its middle.
- [ ] In VR, stand under a can → its height (4.6 m across on its legs) and the tubes (2.4 m across, their axis 2.6 m up) feel right.

## The Moon's historic sites ([#1939](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1939))

- [ ] `--bodyshot`-style trip to Apollo 11 (0.674° N, 23.473° E; `--moonsites apollo11` puts you there), step in (T) and walk to the descent stage → you bump into its body and footpads, not through them; the knocked-over flag lies a few metres off; the footpaths look like faint trodden bands, not painted lines, and never flicker into the ground as you walk over them.
- [ ] At Apollo 15, 16 or 17, find the rover parked ~90-150 m east of the lander and follow its tracks out → two thin parallel bands that keep to the ground over crater rims.
- [ ] Walk up to an S-IVB crater (e.g. `--moonsites sivb14`) → a fresh bowl ~35 m across with a raised rim, bright rays fading out across the ground, twisted pieces on the rim.
- [ ] In VR, stand next to the Apollo 17 descent stage → the scale feels right (body ~3 m up, footpads ~9 m apart diagonally), nothing floats.

## Deck junction caps collide ([#1865](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1865))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --date 2027-07-15`, Cartoon.

- [ ] Walk south across Pont des Bergues through the branch to Île Rousseau (`--at 2500310,1117990`) → you stay on the deck all the way, no fall into the Rhône at N 1117986-1117972.
- [ ] Drive west on the Major across Place des Nations (`--at 2500300,1119935`) → the car crosses the bridge over the railway without dropping or stopping.

## A facade door is drawn only where it opens ([#1766](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1766), [#1767](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1767))

In a window, `--chunks D:/UnitSportSwitzerland/terrain_chunks`, Cartoon, on foot in Geneva's old town (`--at 2501414,1117284`), flat and `--frame earth`, with a fresh user:// (or `user://door_locks` deleted).

- [ ] Walk the streets for a few minutes and press E at every door you pass → every door opens; none says "This door is locked". A side door that had no way in vanishes about 2 s after you first come within ~160 m, far enough off not to catch the eye; note any that goes close by.
- [ ] Go in by a door, look at the facade walls from inside → every door seen from the street has its doorway inside.
- [ ] Quit and start again at the same spot → the doors that vanished are absent from the start.

## The earth fill's biomes outside Switzerland ([#1809](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1809))

In a window, `--frame earth --date 2027-07-15 --time 12:00` (local time), the spectator at full speed, the globe on.

- [ ] Land in the Sahara (`--at 1829400,-515818 --origin 1829400,-515818`) → sand, a few low shrubs, no farms, no green. From 350 m, note how much paler the globe draws past the loaded leaves (sRGB ~243,233,230 against the fill's ~233,220,199 on Nov 13).
- [ ] Fly to Kazakhstan, Yamal, Greenland and the Amazon (LV95 6653598,2940530; 5034504,4850762; 902899,4677347; -6055442,-697411) → dry grass with scattered shrubs; olive tundra with dwarf shrubs; ice; a broadleaf canopy with clearings.
- [ ] Fly from Geneva out of the generator's map (west of LV95 2,385,000 or south of 974,000) → the Swiss look fades into the biome over ~20 km, no straight seam; Geneva, Lavaux and the Salève look as before.

## The globe's 20 km handover ([#1803](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1803))

In a window, `--frame earth --chunks D:/UnitSportSwitzerland/terrain_chunks`, Cartoon, the spectator (Shift+T) over the coast at LV95 -5011173,8685903.

- [ ] At the spot's night, climb slowly from 14 km to 26 km and back → the ground darkens from moonlit to night over 15-20 km with no jump at 20 km; the coast and sea stay where they are, no green land over the sea below 20 km.
- [ ] The same by day → no jump in colour or relief at 20 km; the photo's sea and fields fade in over 20-25 km.
- [ ] While spectating over the ground with the body falling below → no "open WINGSUIT" prompt.
- [ ] Over Agadir (LV95 909680,-472439) and the Sahara, 2-25 km by day → sand coloured, never green or snow-white; the coast follows the ground, not kilometre steps; one sea colour across tile edges.
- [ ] Facing the horizon from 10 to 60 km, day and night → no grey band with a hard edge between the sky and the Earth's limb.
- [ ] Over Geneva's harbour at ~600 m looking up the lake → note whether the near water and the far lake still meet at a hard line (the far leaves now take their own depth).

## Doors, interiors and parked cars far from Switzerland ([#1799](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1799))

In a window, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at -4679383,7259173` (the generated village where the FARM CO-OP stood turned), or fly the spectator there.

- [ ] Walk up to a house door, open it (E / pad A), look through, walk in and back out, in first and third person → the open leaf, the doorway and the rooms seen through it line up with the facade; you step through the doorway, not the wall beside it.
- [ ] Open a garage or barn door and drive a car through it → its leaves swing on the facade, the car passes the doorway.
- [ ] Look at the parked cars along the street and wake one (get in) → they stand along their bays and road, and the one you wake keeps its heading.
- [ ] At a far building site: climb a tower crane's ladder, sit in the cab, set a pallet down with the hook, and work a self-erecting crane from its remote → you face the mast on the ladder, the jib in the cab, the mast at the remote; the pallet lands along the jib.
- [ ] Walk up to a site's skip or rebar and look at it, then at another → the prompt and outline go to the one you face, the outline square on it.
- [ ] Get into a car in a far underground car park (or a hall's forklift) → it wakes facing the way it stood.

## `/time` where you stand ([#1804](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1804))

In a window, `--frame earth --chunks D:/UnitSportSwitzerland/terrain_chunks`, spectator far from Switzerland (e.g. `/tp -4679383 7259183`).

- [ ] `/time set day`, `noon`, `sunset`, `night` there → morning daylight, the sun high, the sun on the horizon, dark; `/time` → "It is … UTC-7 (… Swiss time)".
- [ ] Back in Switzerland, `/time set noon` → 12:00, as before; `/time set 12:00 utc` → 13:00 or 14:00.
- [ ] Online on a server, as admin far away: `/time set noon` → the broadcast names your zone and "noon where they stand", and a second player at home sees the same clock.

## The debug menu on the earth world ([#1820](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1820), [#1743](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1743))

In a window, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500956,1117569 --date 2027-07-15`, F9.

- [ ] Click Leaf edges and Leaf labels, fly round Geneva and out over the fill → every leaf outlined on its ground (green furnished, yellow ground only, grey loading; fill dashed), labels on the near ones, new leaves outlined as they land.
- [ ] Hover each switch → its tooltip names leaves, the globe and the anchor, not tiles; Earth curvature is greyed with its reason.
- [ ] Freeze leaf streaming, fly 3 km away and look back → the loaded leaves stand alone; unfreeze → the leaves round you come back.
- [ ] Wireframe → the leaves come back as lines within a few seconds (slow, as on flat); Normal → as before.
- [ ] Type `/debug tiles,labels` then `/debug off` in the chat → the tools switch on, then all off.

## A join from the menu reaches the ground ([#1816](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1816), PR [#1819](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1819))

The exported Windows build, joined from the title screen (saved server or address), not `--connect`.

- [ ] Delete `chunk_2583_1113.terr` (and its neighbours `2582-2584_1112-1114`) from `chunk_cache` (keep the `.terrc`), join `unitsport.infrack.ch` from the menu as a returning player → the loading screen ends, on foot at the spawn or where you fell asleep; no `[spawn] no terrain streamed under the spawn point` warning in the log.

## Shift no longer stays held after it is let go ([#1805](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1805))

On Windows, a real keyboard, in a window.

- [ ] In the spectator (Shift+T), hold Shift a second or more and let go, twenty times, some while turning the mouse, some during a load hitch, some after an Alt tap with the cursor free → the camera stops sinking every time; a `[input] Shift repeated after its key-up: released` line in the log is that case caught.
- [ ] On foot, sprint with Shift+W and let go of Shift first, then W, a dozen times → back to a walk every time.

## The spectator camera's speed and readout ([#1797](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1797))

In a window, `--frame earth --chunks D:/UnitSportSwitzerland/terrain_chunks`, Shift+T (pad: hold D-pad ↓) into the spectator.

- [ ] With a pad: tap RB / LB, then hold each → a notch a tap, a smooth ramp held (2 m/s to 100 km/s in ~4 s); the readout top right; the hints list Faster RB / Slower LB.
- [ ] In a headset: the right / left trigger while spectating → faster / slower, the readout readable on the VR panel; the triggers do nothing else there.
- [ ] Climb from Geneva to ~300 km at full speed, fly east to Asia (~1 min, ~10 s boosted), come down → the speed grows with the altitude by itself, nothing freezes or hitches on the way, the generated ground is under you when you land.

## Back into the body after a long spectator flight ([#1814](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1814))

In a window, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks`, Shift+T into the spectator.

- [ ] Fly to Asia through Space at full speed, come down, press T above 50 km and keep descending → the body takes over once the ground is in, standing upright on it, the world drawn round it; no `create_frustum_points` in the log. If a `[camera] ... could not be drawn` or `[player] ... a non-finite ... was put back` line shows, the view is still drawn: send that line (it names the cause).

## The earth fill's walls and ledges north of Riddes ([#1788](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1788))

In a window, the earth playtest's launch (`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2578113,1115406 --date 2027-07-15`), the rebuilt pyramid on D: (`cube_global`, Nov 13).

- [ ] Fly from Riddes north over the generated slopes (2578980,1116051) and south over the meadow at 2579661,1108305, low and at 1.5 km → no pale wall along a straight line or a V anywhere in the generated ground.
- [ ] Climb to ~1.8 km over 2580090,1116823 and look NE while the far ground loads, then fly towards it → no wall or overhang where the finer ground takes over; a step of a few metres at most where the bare far ground meets the forested leaves.

## `/locate + parking entrance` on leaves ([#1772](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1772))

Offline, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2502137,1117793`.

- [ ] `/locate building + parking entrance`, then `/go` on the first answer → you land at a garage door with its access road (no longer at 2502137,1117793, whose earth building is a shop without one); a car drives down its ramp.

## GPX replay and its video export on leaves ([#1752](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1752))

In a window, `--frame earth` with a real recording on real leaves (e.g. `--leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --gpx <a ride in the region>`), each against the same without `--frame earth`.

- [ ] Watch the replay in Chase, then press C into Absolute Cinema and Absolute Racing → the runner and the course line lie on the ground; Cinema cuts to the climbs and corners ahead (the log says `[cinema] ... N tiles, M events` with tiles > 0).
- [ ] Turn the road snap on (R) on a ride along roads → the status says on, the runner keeps to the road.
- [ ] Export the video at 30 fps → every frame has its ground and buildings (no blank or half-loaded frames at the start or along the route); the file plays. Known: no sightline cut through buildings and no tile prefetch on the leaves yet.

## Ski slopes and marina boats on leaves ([#1735](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1735))

Offline, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/row7/cube --chunks D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks --at 2583954,1109157 --date 2027-01-15` (La Tzoumaz); and `--chunks fixture:fun --frame earth`.

- [ ] Ski down a run at La Tzoumaz on skis with a pad and in VR (keyboard validated in the earth playtest, [#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)) → the HUD names the run, the snow under you grips as corduroy or ice as on flat, the poles line both edges and none stands doubled where two leaves meet; the corduroy has no seam at a leaf's edge.
- [ ] The same in a Quest (the standalone shader path) → the slope is drawn where the ground is, not shifted by 500 m.
- [ ] On `fixture:fun`, walk to the jetty off the beach and aim at a moored boat → it wakes and you can drive it; it comes back at its berth a while after you leave it.
- [ ] On skis at La Tzoumaz on earth (row-7 leaves as above), `/race` on skis with a friend online ([#1737](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1737); the ski map was validated in [#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)) → the race goes down the runs.

## Furnished leaves under `--frame earth` ([#1613](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1613), [#1510](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1510))

Offline, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500956,1117569` (Geneva's centre).

- [ ] Walk the streets, then drive a car through them for a few km → roads, paint, signals, buildings, trees and the lake are drawn as in the flat world; you stand and drive on the roads, never sunk into them, and the ground never shows above a road. Failed in the earth playtest ([#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)): [#1768](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1768), [#1769](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1769), [#1770](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1770), [#1771](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1771), [#1772](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1772).
- [ ] Walk to a house's door and press E (pad and VR too), go in, come out → the door opens, the interior stands behind it, the way out lands on the street. Failed in the earth playtest ([#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)): [#1765](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1765), [#1766](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1766), [#1767](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1767).
- [ ] A garage door with an access road, a door with a stoop → drive up the access road, climb the steps (both collide).

## Sites, yards and walking in on leaves, online ([#1707](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1707), [#1704](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1704))

A server `--frame earth --generated-world` and a client `--connect 127.0.0.1 --frame earth --generated-world --at 2588796,1118441`; a second client beside it.

- [ ] Walk up to a building site's skip and press E (pad and VR too) → the panel opens; the other player sees what you take go.
- [ ] Open a site office's door and walk in; in an apartment block ride the lift with the other player in the cabin → you walk through the doorway (no wall), both ride; nobody is sent back up from inside.
- [ ] Look at the parked cars and the site machines round you → they stand on the ground, turned as the bays and the road; aiming at one wakes it.
- [ ] Walk round a building site on a hillside leaf ([#1731](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1731)) → its yard is level, its edges blend into the slope without a cliff, and the road beside it is not buried or floating.
- [ ] Join a Battle Royale squad with a bot, go down and out → the bot takes your dogtag to a Postauto stop and you come back.

## Building-site pits on leaves ([#1687](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1687), [#1653](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1653))

Offline, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks`, at a real site at its foundations (`--constructioncheck` lists them), in a window.

- [ ] Walk to the pit's edge, look in, climb down the ramp, walk round the floor → the ground ends on a straight line at the sheet piles, brown walls down to a gravel floor, no ground over the pit and no gap round it.
- [ ] Mount the excavator, drive down the ramp and back up → it drives on the ramp and the floor, never falls through.

## The earth ground's look (PR [#1642](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1642), [#1634](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1634), [#1632](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1632), [#1633](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1633), [#1635](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1635))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500650,1117640` (Geneva), in a window, against the same without `--frame`/`--leaves`.

- [ ] Walk the Jardin Anglais and the Rhône's banks in Realistic− and Realistic+ (`/style`; Cartoon validated in [#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)) → grass, paving and stone banks look as in the flat world (painted grass, textures in Realistic), never pale mint or white.
- [ ] In the headset (PC VR, then the Quest at Very low): the same lawn and lake → the ground in the standalone look, no seam lines.

## The earth world's ground frame turns ([#1612](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1612), [#1512](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1512))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500956,1117569` (Geneva), in a window. Each re-anchor (every 3 km) prints `[origin] shift N ... turned: tilt 0.0269°`.

- [ ] Walk, then drive a car, NE for 4 km across a re-anchor → no jump, no hitch in the view or the steering, the body stays on the ground, the horizon stays level. Failed in the earth playtest ([#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)): [#1763](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1763), [#1764](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1764).
- [ ] Patterns ([#1627](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1627)), in Realistic (`--style real-`): drive along a Geneva street across a re-anchor watching the asphalt and the roof tiles, then stand at the lake shore (`--at 2500700,1118300`) with `/seastate gamey` → no texture slides or jumps, the waves keep rolling the same way, a boat on them bobs with the drawn crests.
- [ ] The same in the headset (PC VR, then the Quest at Very low) → the textures hold in both eyes.

## The server on leaves under `--frame earth` ([#1610](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1610), [#1513](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1513))

A server and two windowed clients, all with `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks`, both clients `--at 2500956,1117569` (Geneva).

- [ ] Walk, run and jump beside the other player, with the server also given `--bots 3 --botsat 2500956,1117569` → each sees the other and the bots standing on the drawn leaves (no hovering, no sinking), turning the way they walk.
- [ ] One client teleports to Riddes (`--at 2581500,1113300` on a rejoin) and back → the other sees it go and come back, on the ground both times.

## The fill and fixture courses on leaves, `--frame earth` ([#1614](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1614), [#1510](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1510))

B4's leaves: `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks`.

- [ ] Start at `--at 2578113,1115406` (north of Riddes, the region's edge on a slope), walk and drive across into the real ground and back → no step, ledge or hole where the smooth generated ground meets the real one; a bank of at most a few metres over ~60 m is the hand-over. Failed in the earth playtest ([#1775](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1775)): [#1778](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1778), [#1779](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1779), [#1776](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1776).
- [ ] Fly 10 km out of the region over the fill → the ground keeps streaming under you (generated leaves take ~2 s each with their roads); no real relief stands up through the generated ground; from high up the far mountains are the real ones (the globe's coarser levels, a known gap).
- [ ] `--chunks fixture:straight --frame earth`, drive east 3 km with a car → the ground is there end to end and the car never drops through it (the road itself is drawn with PR 2), a re-anchor at 3 km moves nothing.

## Maps and GPX on the oblique stereographic ([#1581](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1581), [#1514](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1514))

On the real map (`--chunks D:/UnitSportSwitzerland/terrain_chunks`), at Scuol, where LV95's grid north is 2° off true north.

- [ ] The adventure map (M): drag, wheel-zoom from 2 to 128 px/km, pad stick and zoom → it pans and zooms smoothly under the pointer, the relief, fog, borders, places and pins stay on each other (no drift between them at any zoom), your arrow points along where you look.
- [ ] A Battle Royale round near Scuol: the map (M) and the minimap, turning on and off → the image, circles, towns and your arrow agree; with turning off, true north is up (the image's square a little turned); right-click waypoint and middle-click ping land where clicked.
- [ ] On skis at Scuol, the slope map → the slopes under your arrow are the ones you are on, the arrow points along your skis.
- [ ] A real GPX recording (`--gpx <file>`), run and cinema mode → the runner follows the road as before, the ribbon on the ground, the speed and distance read as the recording's.

## GlobalPos holds ECEF ([#1544](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1544))

On the real map (`--chunks D:/UnitSportSwitzerland/terrain_chunks`), at Geneva and at Scuol, where the grid turns 0.95° and −2° from true north: what was laid in metres east and north is now in true ones.

- [ ] Two clients, one at Scuol: walk, drive and fly past each other → the other is drawn where it is and faces its way (no degree's turn, no lag-free jump), a remote car's wheels line up with its road.
- [ ] At Scuol, a festival or errand site with stalls round it, a Schwingen ring, a sword dummy (`/dummy`) → set out round their centre as before, nothing half a metre into a wall; a sword hit lands where the blade is.
- [ ] The adventure compass and the VR watch's notch on a tracked target at Scuol → both point at it within a degree.

## True north ([#1533](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1533))

At Geneva (`--chunks D:/UnitSportSwitzerland/terrain_chunks`, `--tp` to the Jardin Anglais), where true north is 0.9° east of grid north.

- [ ] In an A320's cockpit at the airport, on stand, nose along runway 04 → the PFD's heading tape and the ND read about 046° (the runway's grid 46.5° less 0.9°), the track diamond on the heading when rolling straight; the AP HDG line agrees with the FCU.
- [ ] In the headset, look at the watch (left wrist) while turning on the spot → the bezel's red N stays on true north (about 1° right of the world's −Z here), the notch points at a tracked target.
- [ ] Free roam with the compass on, facing the Jet d'Eau → the strip's bearing matches the debug readout's "facing" and the GPS, all within a degree.

## The space shuttle ([#1494](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1494))

On the real map (`--chunks D:/UnitSportSwitzerland/terrain_chunks --airports <its airports.json>`), offline, at Geneva.

- [ ] Walk up to the shuttle on Geneva's cargo apron (and Zurich's) → it stands there, E gets in; the intro card names the drive's key.
- [ ] Take off at full rockets (Shift / RT), climb steep, gear up → the roar and the plumes, past 60 km the HUD reads ORBIT; Z (D-pad ↑) starts the drive, the hum; the levers set its speed; the stick flies it like a plane; Z again → a circular orbit, quiet.
- [ ] Come home: drive down over Switzerland slowly (lever low), out of the drive below 50 km, glide in on the outer glideslope with the speedbrake, flare at ~200 kt → lands on the runway; does the brick's glide feel right?
- [ ] From orbit, nose back and a short burn → re-entry: the plasma round the belly and nose, the roar, HEAT on the HUD; no damage.
- [ ] Pad: the whole flight on a pad (triggers the rockets, D-pad ↑ the drive). VR: grip the guarded switch on the centre console, right of the commander's knee, and throw it forward → the drive switches (and the R stick ↑ does too).
- [ ] Online, a second client watching → the plumes, the drive glow, the plasma and the sounds on the other's copy.

## `/movespeed` ([#1487](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1487))

On the real map (`--chunks D:/UnitSportSwitzerland/terrain_chunks`), offline, then on a server as an admin with a second client watching.

- [ ] `/movespeed 10`, run across a valley, then `/movespeed 100` → the run is that much faster, terrain keeps streaming in ahead (or says why not), no fall through the ground; `/movespeed 1` is the normal run again.
- [ ] At x5: swim across a lake, climb a crag (Adventure on), fly in creative mode (T) → each is five times as fast; footsteps and head bob still read as a run.
- [ ] Online at x10 while the other client watches → your body runs smoothly over there, no rubber-banding; a non-admin typing `/movespeed 5` is refused.
- [ ] Pad: the chat's "/" picker offers `/movespeed` and its `normal`, `2`, `5`, `10` to an admin; VR: the same through the quick menu's Chat wedge.

## Server regions ([#1456](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1456))

A server on the real map (`tools/wt.ps1 1456 net`, or `--server --chunks D:/UnitSportSwitzerland/terrain_chunks`): its manifest origin is ~45 km from Riddes, so play there now runs in a region of its own (the server log says `[regions] made region ...`).

- [ ] At Riddes, `/bot add 3`, then `/bot drive 400` → the bots walk to a parked car, get in and drive; they bump into parked cars and trunks, not through them.
- [ ] A Battle Royale on the real map with bots (`/br bots 6`) → the bots land, loot crates, fight and use the Postauto stops as before; none falls through the ground or freezes after the landing.
- [ ] Two players, one at Geneva and one at Riddes, with bots round each → each sees its bots walk smoothly; `/bot list` on both; nothing jumps when a player teleports to the other (`/city`).

## Orbits ([#1480](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1480))

In a window with the globe (`--cube-global` on D:, or the shipped levels), offline in a UFO (R, Mounts).

- [ ] `/orbit 400` heading east → the saucer is 400 km up, the HUD reads `ORBIT 7.67 km/s 400 km Pe 400 Ap 400`; the Earth turns under you, a full turn in 92 min; no shake of the saucer or the globe, nothing jumps every 2.6 s when the world re-anchors.
- [ ] `/orbit 400` heading north and stay ~15 min (or `/speed 8`) → over the pole and down the far side: the globe stays right, the saucer steady (the Space frame is built without LV95 there).
- [ ] Climb the spectator or the UFO slowly from 60 to 260 km over Switzerland, looking down → the flat tiles dissolve without a swing or a seam; at 250 km the frame starts to turn (log: `the Space frame turns`) and nothing on screen moves; coming down, nothing moves at 240 km either.
- [ ] The space drive: the stick and Space / Ctrl push gently, boost hard (three g); Pe and Ap move as expected; boost retrograde until Pe is under the ground → down through the air (it slows), the hover takes over at 50 km without a jolt, and it flies home.
- [ ] The same on a pad (L stick, RT / LT, L3 boost) and in VR (the same through the pad mapping: triggers, L stick, stick click).
- [ ] Climbing in the hover past 60 km (Space held for a while) → it keeps climbing slowly on its 12 m/s² against gravity; let go and it falls back, the hover catching it below 50 km.
- [ ] Two players online, both admins, `/orbit 400` together → each sees the other's saucer beside it; crossing the meridian 172.56° W the other may jump up to 31 km for a second or two (known, #1443 G2's wire).

## The globe and the air from above ([#1478](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1478))

With the `--cube-global` region on D: (`D:/UnitSportSwitzerland/cube_global`, or `--globe <dir>`), spectator or UFO, real sky (play, not a shot run).

- [ ] Desktop, live: the spectator from a Lausanne street (`--origin 2538300,1151200`) straight up to ~12,000 km and back, boost held → the globe fades in above 15 km with no pop; the sky darkens to black by 40 km; the generated fill dissolves at 50-80 km and every flat tile at 150-250 km without a hole; nothing jumps at 60 or 50 km; the whole Earth sits under you with the stars; coming down, the flat world comes back in the same order.
- [ ] VR, live (PC VR, then the Quest standalone with the shipped levels 0-2 only): the same climb as far as the headset's flight allows → no flicker or swimming of the globe in either eye; on the Quest the flat world sinks out of sight at its half-way heights instead of dissolving; frame rate holds.
- [ ] A clean clone with Git LFS, no D: data: the globe still draws from `assets/globe` (levels 0-2), softer below ~1,000 km.
- [ ] At 120 km by day, then at dusk and at night → stars by day up there; the limb is a blue band over black; the terminator lies where the real sun puts it (not where the moon is at night).
- [ ] Frame time at 120 km looking at the horizon, and while climbing fast (tiles streaming) → no hitch over ~30 ms.
- [ ] The same on a phone or the Quest (Mobile renderer, maybe a 24-bit depth buffer) → no flicker between globe tiles or against the flat tiles far out.
- [ ] Stay up across midnight at the end of a month (or start a game on the 1st with a save from the day before) → the globe's colour changes to the new month's without holes.

## Account management on the website ([#1360](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1360))

After the deploy with `--setup` (the new Caddyfile), on https://unitsport.infrack.ch.

- [ ] The site's header, signed in: the name is a link to `account.html`. Change the name there → the header and the page show the new name; a taken name says "That name is taken".
- [ ] `account.html` → Email me a link: the reset mail arrives and its link opens `reset.html` (no 404).
- [ ] The dashboard: open an account, Rename it → the list and the detail show the new name.
- [ ] The game, signed in to the official server: Account screen → Manage your account on the website opens the page in the browser. Rename there, come back to the game → the Account screen shows the new name. With a pad: the button takes focus and A opens it.
- [ ] On the deployed server, with its saves from before: join under your usual name → your bank balance, buildings, placed things, farm stand and BR career are all there (the server log says each was claimed). Then `/name` something else and rejoin → still yours; `/br stats`, a stand's sign and the pinball board show the new name.
- [ ] `/admin list` shows names; `/admin add <name>` for an online player and for one who left (last seen); an old admin by name shows "(by name, not seen since)" until they join.

## Player birthdays ([#1423](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1423))

Set your birthday to today on the Account page (title screen, the account button), with a year and "Let other players see it" on.

- [ ] The Account page's Birthday row: pick a day, a month, a year by mouse, by pad and by VR laser; "31 April" says it has no 31st; Clear empties it.
- [ ] The title screen: "Happy Nth birthday, <name>!" under the account name, in amber; the profile picture wears the party hat, its tip not cut off.
- [ ] Signed in: set the date, sign out, sign in on another machine (or after deleting the birthday fields in `settings.json`) → the date comes back from the account.
- [ ] In the world: the party hat on your head (third person); the gift in the pack with a greeting; Open it → a collectible hat, a party hat, a cake, 3 rockets, CHF 30, paper over your head.
- [ ] Wear each collectible hat (and see `--models,test_output/models`, Headwear) → none clips the face or floats; hair under the propeller cap is hidden.
- [ ] Set the cake on a restaurant table (seated and standing), on a terrace table and on grass → it sits on the top; E takes slices, the cake loses one each time; gone after 8.
- [ ] A Polaroid and a phone selfie by the cake → the print has confetti on the border and bunting at the bottom.
- [ ] Light a rocket at night → a minute of fireworks overhead, the finale of rings; a second rocket at once is refused and kept.
- [ ] Two players online, the server on today's date: the joining celebrant's chat line with the age; the other player looks at them → "Wish <name> a happy birthday" on E/Y; the wish → paper and chime for both, the celebrant's toast counts the wishes; a second wish is not offered; `/birthday` lists them.
- [ ] A server with bots free-roaming: a bot within 40 m walks up, wishes you, waves.

## Buildings meet the ground ([#1417](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1417))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2597738,1119166` (Sion, a hillside door that gets a 3-step stoop); Lausanne `--at 2538476,1152508` (a 12 m plinth).

- [ ] Walk up to the Sion door: the stoop's steps climb on foot (keyboard, then pad) up to the sill, no jump, no catching on a tread; walk off it back down.
- [ ] A downhill house in Lausanne/Zermatt: no dark slot or daylight under its uphill-to-downhill walls, the plinth reads as a basement storey, in Cartoon and PS1.
- [ ] Fly out over the building rings (strides 2, 4, 10, about 1-3 km): no sliver of daylight under a house at the ring change, and no footing poking out of the ground.
- [ ] A door whose ground rises in front of it: the sill sits on a level landing, the door opens and you walk in with the interior floor at the sill.
- [ ] Realistic style: the plinth wall and stoop show plain masonry, not a roof texture.

## Pool ([#1324](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1324))

BowlinGland (Gland): `--chunks D:/UnitSportSwitzerland/terrain_chunks --origin 2510000,1140000 --enter bowlingland`, a pool table by the bar.

- [ ] E at a table → a place, the camera down behind the cue ball (ball in hand from above for the break); A / D aims, Shift fine, W / S follow or draw (the prompt shows it); hold Space and let go → the power swings up and down and the shot goes off at the power shown.
- [ ] The same on a pad (left stick aims, right stick nudges, A strikes, B steps back) and on a phone (stick aims, Bowl strikes).
- [ ] E again alone → a bot sits opposite and shoots on its turn; the balls roll and turn, the stripes read as stripes, the clack, cushion and pocket sounds play; the HUD line says whose turn and what the last shot did.
- [ ] VR: E by grip at the table, then the pad's stick and A through `XrPad` play it (holding the cue is a gap).

## Darts ([#1324](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1324))

BowlinGland (Gland): `--chunks D:/UnitSportSwitzerland/terrain_chunks --origin 2510000,1140000 --enter bowlingland`, a dartboard on a wall.

- [ ] E within the throw line → the board fills the view, a yellow ring sways over the treble 20; W / A / S / D moves it, Shift finely; hold Space → the sway settles for a second or so, then grows; let go → the dart flies in an arc and sticks where the ring was (give or take a few mm), the thunk plays, the HUD says what it scored.
- [ ] Three darts, then the next player; a bust goes back; the checkout hint (e.g. "out: T20 D20") is right; E alone adds a bot that throws its visits.
- [ ] The same on a pad and a phone; VR through the pad (throwing by hand is a gap).

## Table football ([#1324](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1324))

A bar with a table (`/enter` a pub or a bar; the arcade's grouped tables too).

- [ ] E at the table → your side (red first), looking across; W / S slides your rods towards and away from you, Space kicks (the ball shoots up the table), Shift lifts the rods; the ball is served from the side; a goal moves a bead and says the score; first to 10.
- [ ] E again alone → a bot takes blue and plays; online with a friend, each sees the other's rods and the ball smoothly (it's streamed 20 times a second).
- [ ] Pad and phone the same; VR through the pad (gripping the handles is a gap). Does it feel playable, and are the kicks strong enough?

## Air hockey ([#1326](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1326))

An arcade (`/enter` one; Billard Bluff, Geneva, has one on its ground floor).

- [ ] E at a table → your end, looking down it; the stick drives the mallet as you see it, Shift for full speed; the puck clacks off the mallet and rails, a goal buzzes and counts on the scorer; first to 7.
- [ ] E again alone → a bot takes the other end; online, the other player's mallet and the puck move smoothly.

## Arcade coins, tickets and prizes ([#1326](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1326))

An arcade, with some cash in your pocket (`/enter` one; Billard Bluff, Geneva).

- [ ] E at the change machine → 5 CHF become 5 Batzen (a clatter, a toast); with less than 5 CHF it says so.
- [ ] At air hockey, Jump → a Batzen goes in and the puck comes out; with none, it points you to the change machine. At the game's end, tickets print into your pack (two a goal, eight more for the winner).
- [ ] E at the prize counter → the panel: your tickets, each prize and its price, the ones you can't afford greyed with how many more; Swap gives the prize (it's in the pack, the plush throws like the Blåhaj). Pad and VR pointer work it; Esc / B / E closes it.

## Claw machine ([#1326](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1326))

An arcade's claw machines (by the door), with a few Batzen.

- [ ] E at the machine's front → looking in; Jump puts a Batzen in (the countdown starts at 30), the stick steers the claw as you see it, V looks from the side to judge depth, Jump drops: down, closed, up, back to the chute. A win drops the plush into your pack with a jingle; a near miss says so.
- [ ] Does it feel fair? (right over a prize wins about half the time, a few cm off much less, and a prize can slip on the way up.)

## Arcade machines' games ([#1326](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1326))

An arcade with uprights, a racer twin, a dance machine (Pixel Paradise in the fun world has them all).

- [ ] E at an upright → its screen fills the view, attract mode (title, INSERT BATZEN, high scores by turns); Jump puts a Batzen in and Snowball Patrol (or Col Rush, by the machine) starts on the screen; play it to the end → tickets, and your name in its high scores; a friend online sees "NOW PLAYING" with your name, then your score.
- [ ] The racer twin's Col Rush (steer, forward faster) and the dance machine's Yodel Beat (the four directions on the beat): are they fun for a minute, and readable on the screen at that distance?

## Zoom-aware detail ([#1433](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1433))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2538500,1154500` (above Lausanne), the camera item in hand.

- [ ] Aim the camera and zoom from 35 to 200 mm and back over the town and a forest → no holes in the forest (trees neither 3D nor billboards), no flicker at the zoom steps beyond the usual crossfade band; far houses, signs and parked cars appear while zoomed.
- [ ] At 200 mm, pan slowly across the town for ~20 s → no stutter beyond the tiles refining; the terrain sharpens in the new direction within a few seconds.
- [ ] Binoculars (9°) and a gun's aim do the same; leaving the aim, nothing stays refined in a way that costs frames for long.

## No CD burns on an online client ([#1414](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1414))

An exported build, a fresh `user://` (so the default CDs are still to burn), joining a remote server alone.

- [ ] Quit, start offline → the default CDs burn then; the game and its sound stay smooth while they do (a few cores only).

## Wide tile heights, France terrain ([#1400](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1400))

Needs a v3 region (`tools/build-france.sh test mont-blanc` on the NAS, copy `terrain_chunks_france_test/mont-blanc` and point the game at it with `--chunks`).

- [ ] Start the game on the Mont Blanc tiles, walk to the highest cell (about 4,779 m): does the ground reach it (no flat top at 4,700 m), the terrain look right and the Swiss region still load unchanged afterwards?
- [ ] Camargue tiles (`camargue`): the lowest ground is below 0 m (-1 m) and the player stands on it without falling through.

## Sit on chairs, benches and terrace seats ([#1385](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1385))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499794.3,1117466.5` (Trois Verres, Geneva).

- [ ] A terrace chair, E → seated facing the table, the figure on the chair (third person too); walk off → up.
- [ ] Seated at the table, E → a fondue on that table, not standing up; the hint says Stand up on the jump; Space → up.
- [ ] Inside a restaurant: a table's chairs, a banquette, a bar stool, a high table's stool → each seats you on it, not in the air or in the table.
- [ ] A home: a sofa, an armchair, a dining chair; a church pew; a bench.
- [ ] Pad: Y sits, the stick or A stands. VR: grip a chair → seated.
- [ ] Online: a second player sees you seated on the chair.

## Fondue at a table ([#1384](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1384))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499794.3,1117466.5` (Trois Verres, Geneva), adventuring on.

- [ ] Walk the street past restaurants → no "Start a fondue" prompt anywhere but at a table.
- [ ] Stand at a terrace table, E → the caquelon stands on that table; a friend's E there sits them at it.
- [ ] Inside the restaurant, at a table, E → the same; at the bar or in the doorway → no prompt.
- [ ] Pad and VR: the same interaction where E works (the pot's existing button).
## Venue music from the street ([#1386](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1386))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499794.3,1117466.5` (Trois Verres, Geneva), and any bar or club at night.

- [ ] Stand at a venue's door → its music clearly heard, quieter than inside; walk away → it fades and dulls within some 20 m.
- [ ] Two venues side by side → each heard at its own door, neither drowning the street.
## Venues and shop units: their own door and a glazed front ([#1327](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1327))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499805,1117476` (Trois Verres, Geneva; `--venuecheck --venuedoors "Trois Verres"` prints its door).

- [ ] Walk the street past a block with shops under flats → each shop has its own door and a glazed front of 1.1 m bays, a stone plinth, a fascia; no window of the facade's grid peeks round the glass.
- [ ] Walk in by a café's or a restaurant's own door → straight into its guest room, no lobby; inside, its windows stand where the bays are outside.
- [ ] Look through the panes by day and by night → a room behind, tinted to the venue, warm at night; the name sits on the fascia, an awning clears it.
- [ ] Trois Verres → its door on the school's west wall; its room is narrower than its front (a known limit): judge how it reads.
## Fun world ([#1397](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1397))

Play solo > Explore > world "Neon fun world" (or host it). Spawn on the plaza.

- [ ] The look: violet sky with stars, the striped retro sun on the western horizon, wireframe mountains all round, the grid floor beyond the course, neon edges on the roads and venues, palms and lamp posts. In Cartoon, PS1 and Realistic (`/style`): anything washed out, too dark or flickering (the floor's edge at the course's border)?
- [ ] Venues: enter Strike City (bowl a frame), Laser Palace (a showing on), Pixel Paradise (pinball), Café Miami and Sunset Diner (terrace tables), Club Turbo.
- [ ] Race track: take a pit car, `/race start` near the track; the kart loop with a kart. Is the 14 m track width right, do the neon edges help or distract?
- [ ] Grand circuit (all round the course): take a car from the grid on the south straight, `/race start` there: does the race follow the whole lap, are the chicanes, the esses and the crests fun at speed, is anything too tight for its radius?
- [ ] Airfield: take a small plane from the apron, fly the air rings from the runway's west end: does the next ring read (pink, larger), is the panel on the right readable, does the time stop at the last ring? On a pad, and in VR (the panel is not shown there).
- [ ] Snow hill: skis or snowboard on the slope, the T-bar up.
- [ ] Neon Arena: the concert plays whenever you come.
- [ ] Festival field: Fahnenschwingen, Schwingen ring, alphorns, the stone all open.
- [ ] Stunt park: kickers, gap jump, half-pipe and bowl with a car, a motorbike, skis: fun or frustrating, too steep, too small?
- [ ] Truck yard: reverse the Scania into a dock bay between the trailers, back the tractor under a trailer between the containers, the L alley, the drawbar train's box; the pickup's speedboat down the slipway.
- [ ] PvP block (hosted, with a friend): weapons from the crates hurt only while both stand inside the red outline; nowhere else.
- [ ] Leave to the title and play the open world: no fun world venue, arena, festival field or dusk left behind.

## Phone: interact pills, pause menu ([#1354](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1354))

On the Android phone (a test APK).

- [ ] Walk up to a building's door, a shop counter, a loot cupboard: a cyan pill names it ("Open the door") on the right edge; a tap does it.
- [ ] II (pause) on a phone held sideways: every entry down to Quit can be reached (it scrolls when it does not fit), Leave to main menu works.

## Loading and PC responsiveness ([#1382](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1382))

- [ ] Load the open world offline and join the server with a browser, a video and the task manager open: does the PC stay responsive while loading (mouse, windows, video), does the loading screen stay near 30 fps, and does the game's own frame limit come back once in the world? → nothing stutters outside the game; the world still loads about as fast.

## Faster rejoin ([#1387](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1387))

The real server (unitsport.infrack.ch, HTTP mirror and Linux deploy), which loopback cannot stand in for: join it twice from the same install, the cache kept between.

- [ ] First join, then leave and join again: the log shows `index of the last visit to this server merged at boot`, `the copy from the last visit is still the server's` for the horizon and places, and one `[horizon]` line, not two. Is the second spawn clearly faster, and does the horizon fill in behind you instead of holding the loading screen?
- [ ] After the server is updated (new tiles or a new `horizon.bin`), join again: the log says the index changed and the files stream, nothing stale is drawn.
- [ ] Join an older server (no `AssetKind.Digest`): it joins as before, one "not available on the server" line for the digest.

## Sword combat ([#1374](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1374))

`/spawn sword` (admin), put it on the hotbar, `/dummy`; on a server also `/pvp on` with a friend, `/bot add 1`, `/bot arm sword`, `/bot swing`.

- [ ] Feel of the combo: five taps in the rhythm chain the five distinct swings (diagonal, backhand rising, spin, thrust with a lunge, jump and slam). Does each read as its own move from behind in Cartoon and PS1, are the wind-up, the whip and the settle readable, is the lunge a good size and stopped at the target?
- [ ] Standing still versus walking forward gives two sets; do the repeats look varied, not looped? Does the body blend in and out of walking without a snap?
- [ ] Charged blow (hold after a swing, let go after about half a second): crouched wind-up and spinning release; plunge from a ledge: tucked fall and the kneel; dodge in each direction (roll forward, backstep, sidestep).
- [ ] Hit feel: hit-stop, shake (try the screen-shake setting), pad rumble, damage numbers and the dummy's recoil; is the finisher's weight right, is it too much on repeat?
- [ ] The blade's trail through the active frames: visible and not distracting, in Cartoon and PS1; the backhand mirrors it.
- [ ] Lock-on: Middle button / Z / R3 locks the target nearest the view, the camera eases round, the body faces it; does a soft lock turn a swing to a target a little off the aim?
- [ ] Pad: RB swing, B dodge (the pad's crouch and slide are off while the sword is drawn), R3 lock (the view switch is off). VR: trigger swing, B dodge, R3 lock, and the stamina bar is missing there.
- [ ] Hits are the blade touching: does a hit ever count when the blade visibly misses, or a visible touch not count (try the edge of the reach, a moving target, a dummy and a player)? `--sworddebug` shows the capsules.
- [ ] Impact frame: a finisher and a full charge flash the screen with speed lines and punch the view in; Settings > Impact frames turns it off. In VR there is none (only the hit-stop and the spark): is that enough?
- [ ] The grip: the handle sits in the closed fist, no sliding or popping through the combo; the free hand is a quiet counterweight, goes to the handle on the finisher and the charged cut and back without a pop.
- [ ] Auto-holster (#1369) with the sword: it is not put away by a dodge (Shift shares the sprint) nor mid-blow; after about 8 idle seconds it is holstered and Aim or comma draws it again.
- [ ] With a friend: they see your swings and the same variants, the dummy's hits, your knockback on them; a dodge in time avoids a blow.

## Wheel zoom, holster, fading hotbar, hand chip (PR for [#1369](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1369))

On foot in third person, anywhere (`--world fixture` does).

- [ ] Sprint (Shift) and turn the wheel: the camera zooms in and out, the hotbar does not move. Alt + wheel steps the slot; releasing Alt after a wheel step does not free the cursor (a lone Alt tap still does).
- [ ] Press the number of the slot in hand, and `,`: the item leaves the hand and comes back. Does the chip (bottom right) read well dimmed as "holstered", and "Empty hand" on an empty slot, over snow and over a dark scene?
- [ ] Leave an item alone for 8 s: it holsters by itself. Does 8 s feel right? Every item goes away, the hammer, rod and detector too; aiming, a camera held up and a cast line do not.
- [ ] Start sprinting (Shift / L3) with an item in hand: it goes in the pocket at once and stays there after the sprint; Use draws it on the run, and a new sprint start holsters again. Does it feel right (not annoying when you want the item while running)?
- [ ] Pad: tap D-pad right steps the slot; hold it (about half a second, a tick) holsters or draws. Does the tap feel late now (it fires on release)?
- [ ] VR: hold the grip at the right hip for a third of a second (buzz): the item goes away, again it comes out. A quick squeeze there still steps to the next slot.
- [ ] Settings › Gameplay › Hotbar: Always, Fade out (default), Hidden. In Fade, change slot / holster / use an item / open the quick wheel: the bar shows for about 3 s then fades smoothly. Is 3 s right?
- [ ] A second player sees your hand empty when you holster and the item come back when you draw.
- [ ] Touch (phone, or `--mobile`): the hotbar stays always visible and there is no chip.


## Fasnacht ([#1364](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1364))

`--festival test --date 2027-02-04 --world fixture` (Luzern's: the Larven stall 9 m west of the fixture's start, the Guggenmusik stage 14 m east and 6 m north); `--date 2027-02-15` and the clock at 04:30 (`--time 4.5`) for Basel's Morgestraich with its lantern 11 m north; or online on the western map in the real dates (easter-52 for six days, easter-41 for three, relocated to the region's largest town).

- [ ] Look at the six Larven on a figure (Waggis, Alti Dante, Harlekin, Teufel, Fritschi, Ueli: model viewer Figures / Hat - Larve...), in Cartoon, PS1 and Realistic: do they read as faces at game distance, is the Waggis's nose funny and not scary?
- [ ] The Larven stall: E takes a mask and a bag of Räppli once a day; the mask goes on from the pack; others see it. With nothing on the head, the festival's own mask (Fritschi in Luzern's window, Ueli in Basel's) is worn.
- [ ] Räppli: Use with the bag in hand throws a handful; does the paper look and fall like confetti in all three styles, does a second player near see it, is the toss sound good? Ten handfuls pay 3 Batzen.
- [ ] The Guggenmusik: E at the stage, the count-in, tap on every note (LMB, pad RT, VR trigger). Is 120-140 BPM playable on the drum (40 notes in 15 s) on the pad and with the VR trigger? Do the snare, bass drum, trumpet and sousaphone sound like a Gugge (loud, a bit sour)? With two friends on the other voices, does "in sync" feel earned (the din's result line)? Esc leaves; walking away ends the song.
- [ ] Basel's Morgestraich at 04:00 of day 1: do the street lamps and lit windows really go out (houses, street lamps, props) while the lanterns shine, is it dark enough to be an event, and does the drum roll with the saying land? At 05:30 do the lamps come back?
- [ ] The stage, the stall and the lantern as props at night and by day (Cartoon, PS1, Realistic): readable, not z-fighting.
- [ ] VR: a grip at the stall, the lantern or the stage works through the pad route; tapping the notes with the trigger; striking a drum by hand and tossing Räppli by hand are not built.

## Sechseläuten ([#1363](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1363))

`--festival test --date 2027-04-19 --world fixture` (the pyre stands at the fixture's start, the Weibel 9 m east of it; `--sechsepace 1` makes a minute of the burn a second, `/time set 17:59` as admin lights it at once), or online on the western map on the real date (third Monday of April, relocated to the region's largest town).

- [ ] Look at the square: a ten metre pyre of crossed logs with a cotton-wool snowman on top (blue and white sash, a broom, red firecracker fuses round the head, a pointed hat), the Zunft Weibel with his staff and six guild banners. Cartoon, PS1 and Realistic: does it read well, and is the pyre not floating or sunk on a slope?
- [ ] At the Weibel E (pad Y) takes the purse once; at the pyre Use (pad X, VR trigger) steps the minute and E places the bet: is the picker readable, does the coin sound land, is it clear that the panel changes after you bet?
- [ ] The light at 18:00: the whoosh and bell, flames rising over about fifteen seconds with smoke, the glow on the square at night. Do the flames look like fire in each style, and are the billboards not clipping the logs too visibly?
- [ ] The bang: flash, the burst of sparks and the boom, the head vanishing, the pyre falling to glowing embers. Is it a satisfying moment, loud but not harsh? Is a bet on the right minute worth the wait (a median burn is 78 s)?
- [ ] After the bang E at the pyre grills a Cervelat; eat it (LMB, pad X): warm for 2 minutes. With two players: the board names both bets and the shares are fair by closeness.
- [ ] VR: a grip at the pyre or the Weibel works through the pad route; dropping a coin or stepping a dial by hand is not built.
## Chalandamarz ([#1361](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1361))

`--festival test --date 2027-03-01 --world fixture` (the Treichel stand is 45 m west of the fixture's start, the loop of seven stations round it), or online on the western map on 1 March (Scuol, Zuoz or Guarda, else the region's largest town).

- [ ] Look at the loop: the Treichel stand (wooden frame with five brass Treicheln, a blue board, a straw doll) and seven pennant banners on a ring of 45 m. Cartoon, PS1 and Realistic: do they read well?
- [ ] E (pad Y) at the stand begins; then ring with E (pad Y) on the beat while walking: does the drum at 120 BPM and the closing ring on the HUD feel like the same beat as the Treichel you hear when you press? Is it fair with real latency (Perfect 70 ms, Good 140 ms around the server's beat)? Is walking at a child's pace (1.4 m/s) comfortable while pressing every step?
- [ ] Two people in a party ringing on the same beats: "In step with <name>! +2" and the chime; do both see it, does the party's total on the Journal's Festival tab add up?
- [ ] Is "Keep walking!" / "Back onto the loop!" clear when ringing on the spot or in the square?
- [ ] VR: a grip at the stand begins and a grip on each beat rings (pad route through `XrPad`); swinging the hand like a cowbell is not built.

## Cinema panel: the cinema's films, stars, the server's films ([#1368](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1368))

At a cinema booth's panel (E at the screen half), online on a server where you are admin and one where you are not.

- [ ] On a phone: pick a film in the drop-down, Play the film → the hall plays it from its start, through the server.
- [ ] Pad (D-pad, A) and the VR laser: open the drop-down, pick, Play the film; reach the star and Server's films….
- [ ] Play a web link, press the star → it fills; the drop-down ends with "Starred: …"; close and reopen the panel, play it from there; press the star again → gone.
- [ ] As an admin, Server's films… → a window: a name and In the halls' rotation; Add → the chat says it was added with that name; the drop-down lists it for every player. As a non-admin the button is not there.
- [ ] Rotation off for a film, then restart the showings (screen off and on in a few halls) → halls never pick it by themselves; on → they may.
- [ ] Server's films… on a film already listed → Save renames it, Remove takes it off for everyone.

## 1 August ([#1362](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1362))

`--festival test --date 2027-07-31 --world fixture --pois fixture --august1hour 22.5` on a server, or online on the western map in the real dates (31 July and 1 August; held in Genève, else Lausanne or the largest town). `/time set 22:30` for the night.

- [ ] The ground: a lampion stand (striped roof, a string of paper lanterns), a fireworks stand, a village fire (log pyramid with a flame), the lampion post with its lanterns and a Swiss flag, a stop lantern at each of ten stops on a ring. Cartoon, PS1 and Realistic: do they read, and do the lanterns and flames glow at night?
- [ ] Höhenfeuer: from the village or a summit at night, flame billboards on the peaks round you out to the horizon (about one in four peaks under 2 500 m). Do they flicker, stay readable at 10 km and not pop when you walk? Does a paraglider over one of them climb?
- [ ] Lake fireworks at 22:00 game time: at Genève, Lausanne-Ouchy, Montreux, Neuchâtel, Zürich, Luzern and Lugano do the shells lie over the water, not over the town? (The quays and bearings are read off the map by eye: report any that are wrong.)
- [ ] The lampion: take one at the stand, hold it; others see its red light. Is it a pleasant light in each style and in VR?
- [ ] The walk with friends: two or three in a party at the post at night, E (pad Y) starts it; the HUD shows the stop, its distance and compass point; lag behind and the party is told to wait. Is the loop's length (565 m, ten stops) and the 30 m rule fair?
- [ ] Own fireworks: buy a box (20 Batzen) at the stand, light it at night with the use key (LMB, pad X, the VR trigger): the fuse hiss and a 40 s volley; others within sight play it too; is it loud and big enough but safe for the frame time on a Quest?
- [ ] VR: a grip at a stand or the post works through the pad route; lighting by hand is not built.

## Zibelemärit ([#1376](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1376))

`--festival test --date 2027-11-22 --world fixture` (four stalls in a row 4 m north of the fixture's start), or online on the western map in the real dates (fourth Monday of November, relocated to the region's largest town).

- [ ] Look at the lane: four booths with red-and-black awnings, onion braids hanging at one, tarts on a rack, an iron kettle with a glowing lid, crates of eggs under a bunting. At night do the lanterns glow warm? Cartoon, PS1 and Realistic: do they read well?
- [ ] E (pad Y) at a stall: "Taste at the Onion braid stall (free)" pays 6 Batzen with a bell; E again buys the braid for 6 Batzen with a paper rustle. Is the prompt readable and the outline round the stall right? All four stalls in a day pay the Marktgang.
- [ ] Buy six confetti eggs at the Konfetti stall and throw them (LMB, pad X, the VR trigger): is the burst of paper like the Räppli, does every fifth throw pay with the crack sound, is 16 Batzen a day fair?
- [ ] Eat the Zibelechueche and the Mehlsuppe (LMB, pad X): the soup warms for 3 minutes.
- [ ] VR: a grip at a stall works through the pad route; grabbing a braid or throwing an egg by hand is not built.

## Escalade ([#1352](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1352))

`--festival test --date 2027-12-11 --world fixture` (the Marmite stands at the fixture's start, the pot 9 m east, the start line 12 m west), or online on the western map in the real dates (second Saturday of December, relocated to the region's largest town).

- [ ] Look at the square: a cast-iron Marmite with a chocolate lid and a red-and-white band, marzipan vegetables and a little flag; a copper soup pot over a fire; the start arch; six torch posts on a ring of 55 m. Cartoon, PS1 and Realistic: do they read well, and do the fire, torches and lanterns glow at night?
- [ ] Two people in a party at the Marmite: E (pad Y) as the first says "You hold the Marmite", the other gets "take the other side now!"; the second's E smashes it. Is the saying readable, the crash and crumbs a good sound, the chocolate in both packs? A third party member near it is paid but cannot smash.
- [ ] The soup pot: one E feeds everyone of the party within 12 m; the others see "<name> ladles you a bowl of soupe". Eat the soupe (LMB, pad X): warm for 3 minutes.
- [ ] The Course: E at the start arch begins it with the horn; the panel counts the time and shows the next gate with its distance and compass point; the torch posts are easy to find; a bell rings at each gate and at the line. Is par (a good jog) fair, are the gate rings of 6 m forgiving?
- [ ] VR: a grip at the Marmite, the pot or the start line works through the pad route; gripping the lid or the ladle by hand is not built.

## Christmas market ([#1349](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1349))

`--festival test --date 2027-11-30 --world fixture` (the market stands at the fixture's start; `--date 2027-12-06` for the Samichlaus's day), or online on the western map in the real dates (Montreux, Grand-Rue).

- [ ] Walk into the market's lane: two rows of four stalls with the tree at one end and the Samichlaus at the other. At night do the lanterns, garlands and baubles glow warm? Cartoon, PS1 and Realistic: do the stalls, the tree and the Samichlaus read well?
- [ ] E (pad Y) at a stall: "Taste at the Glühwein stall (free)" pays 8 Batzen with a ting; E again buys the mug for 4 Batzen with a clink. Is the prompt readable and the outline round the stall right? Does a stall's counter face the lane on both rows?
- [ ] Drink the Glühwein mug (LMB, pad X): the mouth pose and the gulp look right; Settings > Gameplay > Alcohol off: no glass icon and no effects; real: a mug is 15.8 g, so a few mugs climb the glass icon.
- [ ] Taste at all eight stalls in a day: the last shows "Marktgang!" and the day's stall-hop has paid 80 Batzen; the Journal's Festival tab shows the Batzen and the party's points.
- [ ] 6 December: the Samichlaus's prompt reads "Take the Samichlaus's bag" and the sleigh bells play; the day before it says he comes on 6 December.
- [ ] VR: a grip at a stall tastes and buys (through the pad route); holding the mug to the mouth drinks it. Grabbing the mug off the counter by hand is not built.

## Schwingen at the Älplerfest ([#1315](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1315))

Online (a server and two clients), or `--festival test --date 2027-08-18 --world fixture`; the ring stands 20 m north of the festival's middle. `/schwing bot` works anywhere a bot is near.

- [ ] Walk to the sawdust ring and look at it: the prompt says "Wrestle: Schwingen"; E (pad Y) joins the queue, E again leaves it. With nobody else, a bot joins after about 8 s.
- [ ] In the bout: lean with the stick (screen-right is towards them, up and down lean round), throw with LMB (or X on a pad), hook a leg with RMB. Do the leans, the braced stance, each of the six throws and the thrown body read well from the side-on camera in Cartoon, PS1 and Realistic? Does the lying body sit in the sawdust, not float or sink?
- [ ] When they throw, the banner names the throw and tells how to answer; hold Shift (pad B) and lean the named way, or lean the counter. Is the 0.35 s window fair on a pad and at 100 ms ping?
- [ ] On the ground, mashing the punch key pins them or gets you out; do the winner's brush and the loser's dusting look right, and does the ring bell and thump sound good (sound player: Schwing*)?
- [ ] A won bout pays Batzen and party points (chat line, the Journal's Festival tab); a bout under 20 s or a forfeit pays the forfeiter nothing.
- [ ] VR: the stick leans (forward = towards the rival), B braces, the triggers throw; the head keeps its view. Gripping the belt for real is not built.
- [ ] The Schwingerhosen (jute trousers, Alpine set) wear from the inventory and look right in the Bottom slot (they are not yet sold or found: `/give` one).

## A video on a cinema hall's screen ([#1299](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1299))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499896.6,1117463.7 --detail high` (Cinémas du Grütli, Geneva), walk up to the booth. A dev build needs ffmpeg on PATH (yt-dlp too for YouTube); a release ships both.

- [ ] E at the screen half of a hall's booth panel → the projector panel opens, low on the screen. Paste a YouTube link, Play → within a few seconds the hall's screen, the projector beam, the port and the lens glow show it; its sound comes from the screen and the film music stops.
- [ ] Two players in the same hall (one online, one host) → the same picture at the same moment, the same sound; a third who joins later lands on the same moment.
- [ ] Sound: does the video's level sit right against the hall (too loud, too quiet), and is it heard from the lobby through the door?
- [ ] At High detail, walk into the beam → you wear the video and leave a shadow on the screen. Below High → the pasted picture.
- [ ] Pad: Y at the screen half, D-pad to Paste / a recent link, A → it plays. B closes.
- [ ] VR: grip the screen half → the panel on the VR panel, the laser picks Paste or a recent link.
- [ ] A Twitch live channel and an `.m3u8` live stream → they play (the players a segment or so apart: expected until the relay).
- [ ] Stop the video → the cat slides and the film music come back. Screen off → dark and silent.
- [ ] Perf: F3 Detailed while a 480p video plays in the hall → note the frame time against the slides.

## A cinema hall's video through the server ([#1298](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1298) phase 2)

- [ ] On a server, tick **Through the server** in the projector panel while a video plays → every player's picture turns a little softer (the server's copy), it goes on at the same moment, nobody's restarts.
- [ ] A phone (or the Quest) joins the hall → it shows the video and plays its sound through the server, on time with the desktops.
- [ ] Block the video's domain on one PC (hosts file) and play it → that player gets it through the server after a few seconds; the others are unaffected.
- [ ] A live Twitch channel through the server → every player on it sees the same moment (unlike each fetching it).
- [ ] Server bandwidth: three players watching through the server → about 1.5-2 Mbit/s each in the server's stats; nothing when nobody watches.

## Sharing a screen, a window or a webcam ([#1301](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1301))

- [ ] Projector panel › Share from this computer › a monitor, Sharp → the hall shows your screen about 1.5 s later, for you and the others at the same moment; text on it reads from the middle rows.
- [ ] Share a browser window playing a video, with **PC sound** → its picture (even while the game covers it) and its sound in the hall; the hall's own sound does not come back into it (no echo).
- [ ] Add a microphone → your voice over the PC's sound, both in sync with the picture.
- [ ] Share the webcam (with its mic) → your face on the screen, the mirror the right way for the audience?
- [ ] Smooth on a game or a video → motion looks fluid; note the upload in Task Manager (about 6 Mbit/s).
- [ ] Leave the cinema while sharing → after about 5 s the hall goes back to the slides; Stop sharing → at once.
- [ ] Pad: D-pad through the share pickers (OptionButton popups), A to pick, Share. VR: the laser on the pickers and Share.
- [ ] Play a video file… (an mp4 or mkv from your disk) → the hall plays it from the start, everyone at the same moment, its sound from the screen; at its end the slides come back. A file of several GB starts within a few seconds.
- [ ] **NDI** (needs NDI Tools from ndi.video; OBS's NDI output already checked by the scripts): host a concert and `/concert ndi on`, then in a cinema's projector panel pick `NDI: … (PGM)` → the concert's program camera on the hall's screen. With OBS: its sound heard in the hall, in sync with its picture, the same moment for two players. ([#1303](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1303))
- [ ] Walk into a cinema you never entered → some halls are mid-film: an open film (Sintel, Spring, …) or the cat slides, with its sound; a friend joining sees the same moment. `/cinema pool` lists the films; as an admin `/cinema pool add <a YouTube link>` → "Added …", and after a while new cinemas show it too.
- [ ] Play a 16:9 video, then a scope (2.39:1) film → the side curtains glide in to frame the 16:9 picture and back out for the scope one, smoothly, no jump; a dark opening scene does not close them on the picture.
- [ ] Projector panel › Display: Cover, then Stretch, then Contain with 4:3 and 1.85 → the screen, the beam (High detail) and the picture in the porthole's glass all show the same framing; the curtains glide to its edges; a friend in the hall sees the same.
- [ ] **VR sharp cinema screen** ([#1319](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1319)): in a headset, play a 2D film in a hall → the log says the screen is on a compositor layer; the picture is sharper than with Settings › VR sharp cinema screen off; it stays put on the screen as you walk and turn (no swimming, no lag); a seat back, another player's head, the curtains and your hands cover it where they are in front; Cover/4:3 framing as on the mesh screen; walking out of the hall takes it away; the menus' panel still draws over it.
- [ ] **Cinema video quality** ([#1319](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1319)): Settings › Video › Cinema video Low / Medium / High while a film plays → it restarts in a moment, on the clock with a friend's; High visibly sharper from the front rows; no stutter on your machine at High (CPU).
- [ ] **3D**: play a side-by-side or top-and-bottom film (a name with SBS / TB picks the layout, or the 3D picker) → with red-cyan glasses the screen has depth; untick Red-cyan glasses → the plain left picture. In a headset → each eye its own picture, real depth, no colour fringes. Does the depth feel right (eyes not swapped)? ([#1298](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1298) phase 6)
- [ ] Linux (X11): a monitor share and the PC sound (PulseAudio monitor). macOS: a monitor and a camera share (screen-recording permission asked).

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

- [ ] Walk into an L-shaped house → rooms in both arms (a hall down the far arm with rooms off it), none out in the yard.
- [ ] Look out of a window on an angled wall → the day's light comes in through it like any other.
- [ ] VR: walk into an angled room → the walls hold you (teleport into the corner too).
- [ ] A house drawn on the web with an angled wall (`--house-layouts`, see `net/house-layouts`) → that wall is one straight wall in the game, its door in it.

## Doors placed where the plan has a way in ([#1329](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1329))

Geneva: `--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2499896.6,1117463.7`; `--outlinesurvey --key E_N_I` names a building's doors.

- [ ] Walk round a block of flats → its doors are on the street front, each into its own stairwell lobby; no door on its ends or at the back.
- [ ] Walk into a school or an office with a long wing → the wing has rooms and its own stair; its side doors open into them, and the wing joins the rest through an arch.
- [ ] A works of several halls → each hall wing has a floor, and you walk from one to the next through an arch.
- [ ] A side door on a stretch of facade no room reaches → it opens into a short corridor, square to the door, ending in an arch into the room behind; walk it both ways, no snag at its angled end.

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
- [ ] Hidden crevasses: see the section "Hidden crevasses" below.
- [ ] A mountain hut, a Beiz or a lit campfire with a fondue pot (E, or use the pot item at the fire) → the panel opens, the party sits down; trace an eight with the mouse (hold LMB) and the right stick (VR: the stick; no real fork yet) → the cheese stays smooth; stop stirring on a high heat → it splits with a hiss. Judge the stirring feel and the panel's size on a pad and in VR.
- [ ] Lose your bread (keep dipping) → the forfeit popup (line, buy a round, yodel) answers with 1-3, D-pad + A and the laser; `--forfeits mild` hides the money one.
- [ ] Two-handle gate at a castle, hut or mine (`RewardPlacer` layouts, real region) → two players hold E at the two wheels within 1.5 s and it opens for 60 s; leg-up: crouch at a high boulder, the partner jumps onto the step → 2.6 m climb-jump without stamina.
- [ ] With a veteran (3 summits) guiding a newcomer to a first summit on a real map → both get their cap (Erstbesteigung, Bergführer); the Journal's Collection > Together page shows the stamps, the week's minutes and the mentor table. Judge the page's layout.
- [ ] `--world fixture` is enough for everything except the glacier, the real huts and the gates; the Crevasse, Caquelon, Rope, Harness and Gate models are in the model viewer (`--models`, category Adventure) → look right in Cartoon, PS1 and Realistic.
## Hidden crevasses (#1372)

A real glacier (Aletsch, Rhône, Morteratsch: `--chunks` at real tiles with glacier cover). `Settings › Gameplay › Crevasses` on (default), or `--crevasses on`.

- [ ] Unroped, walk across a glacier for a few minutes → now and then (4 % of 12 m cells) the snow bridge breaks: you fall 10 m into a blue ice slot, unhurt, and the toast says to climb. Judge how often it happens and whether the fall, the dark slot and the sky above read well in Cartoon, PS1 and Realistic (the terrain is not drawn from underneath: look up and sideways).
- [ ] In the slot, push the stick / W into an ice wall → you grab it and climb out (the stamina ring pays); at the top you stand on solid snow, not falling through. Pad: the left stick into the wall. VR: the same through the pad route, or hand over hand (`XrClimb`). Judge the climb speed and whether the top-out is smooth.
- [ ] Wait a minute in the slot without climbing → you are set on the lip (the rescue). Judge whether a minute is the right time.
- [ ] Roped to a standing friend within the rope's length → the bridge breaks, the rope holds you 3 m under the lip (zip sound), you hang; the friend holds E 2 s (pad: hold Y) → you rise out. Your friend sees you in the slot on their screen. Judge the swing and whether the friend can see what is going on.
- [ ] Settings › Crevasses off, or the server started with `--crevasses off` → nothing breaks.
- [ ] A server run (`StaminaWatch` on): the server log shows `is down a crevasse: not watched` and no `implausible climb` for the climb out.
- [ ] The Crevasse model (`--models`, Adventure) and the Crack and Thud sounds (`--sounds`) look and sound right.

## Festival framework, part 1 of the festivals ([#1295](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1295))

Nothing is player-facing yet but the Journal tab and the poles: the check-only festival stands in for the real ones. Offline: `<godot> --path . -- --festival test --date 2027-03-11 --systems ui,physics,occasions,adventure --world fixture` (add `--chunks D:/UnitSportSwitzerland/terrain_chunks` for the real map, where the test festival is held at the Riddes spawn).

- [ ] Press J → the **Festival** tab (between Collection and Travel) lists "Test festival at the test field", day 2 of 5, the bell open, the gong tomorrow, the closing show, the score bar and the Batzen. Judge the layout at 1080p and on a phone-sized window (`--mobile`): readable, nothing cut off but the scroll.
- [ ] Pad: the tab opens with LB / RB; the stick or D-pad scrolls the page (it has no buttons yet) → it scrolls, focus stays visible.
- [ ] Walk round the spawn → a ring of 16 red Swiss-flag poles stands about 60 m out. Settings › Occasions › the test festival › Off → the poles go, while the Journal's Festival tab and the score stay.
- [ ] Real map, dev region, a dedicated server with the same flags → the log says `[festival] running: test day 2/5 at the test field`; join with a client and open the Journal → the same tab, with the same day.
- [ ] Real map: a real festival site outside the region relocates to the largest town ("held this year in Bulle"): needs a real festival, so this waits for part 3 (Älplerfest); the rule is unit-tested only.

## Alcohol: a real blood alcohol level and two breathalyzers ([#1309](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1309))

Offline is enough for most: `--systems ui,physics,adventure,terrain` on any map; `--alcohol-clock 30` makes the half-hours pass faster, `/alcohol test drink 4 40` logs four beers drunk 40 min ago, `/give` the Breathalyzer and the Breath test (or buy them at a pharmacy).

- [ ] Drink three beers within a few minutes, wait (or `--alcohol-clock 30`) → around half an hour in the view starts to sway a little; at about 0.5 per mille driving a car, the steering answers a beat late. Judge whether the sway is subtle, not nauseating, and whether the lag is fair rather than frustrating.
- [ ] At 0.8 and over (`/alcohol test drink 6 30`) → the screen edges blur, walking drifts slowly to the side, and the view feels drunk. Judge the blur strength in the Cartoon, PS1 and Realistic styles and the drift's size.
- [ ] At 1.5 and over (`/alcohol test drink 10 30`) → you stumble now and then (flat for a second), a climbable wall will not be grabbed, and the toast says "Not in this state". Over 2.5 you pass out for a minute and wake. Judge how often the stumble comes and whether passing out feels fair.
- [ ] Settings > Gameplay > Alcohol: **View effects** off → no sway and no blur, the input lag stays. Mode **Off** → drinks carry nothing; **Mild** → half the effects, nobody passes out.
- [ ] VR: drunk, the head does not sway; a soft vignette comes instead, and the lag is the same. Judge the comfort on a headset.
- [ ] Hold Use with the electronic Breathalyzer in hand → 5 s of rising tone in the view pose at the mouth, two beeps, then its screen low in the view: `0.62 ‰ · 0.31 mg/L` and `> 0.5 ‰: do not drive`. Within 15 min of a drink it says `WAIT 12 min`. Pad: RT; VR: RT (bringing it to the mouth by hand is not built). Judge the screen's size and how long it stays (8 s).
- [ ] With a friend online: hold the device out (E, pad Y, or a VR grip near them) → they get an invite, accept with F2 / the invites panel, and both screens read the same number. A server-run bot accepts at once and reads 0.00.
- [ ] The single-use Breath test: blow 5 s, a tube on screen turns green (under 0.5) or amber, then it is used up. Judge whether the colours read clearly and whether "no number" feels right.
- [ ] The glass icon left of the stamina ring appears above 0.3 per mille and nowhere else shows the level. Judge its place against the Together chip and in VR.
- [ ] Models: the Breathalyzer and the Breath test tubes (clear, green, amber) in the model viewer, category Adventure, and in the hand in first and third person → they look right; the sounds are in the sound player under `AlcoholSounds`.

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

## Steinstossen, the Älplerfest's stone put (part 3 of [#1269](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1269), [PR #1318](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1318))

Offline on the check-only festival: `<godot> --path . -- --festival test --date 2027-03-14 --systems ui,physics,occasions,adventure --world fixture --chunks fixture:flat` (its stone opens on day 5). The real Älplerfest runs 14-24 August at Interlaken, Schwägalp or Schwarzsee, or in the region's largest town: `--date 2027-08-16` with the real map.

- [ ] Find the grey stone on the sawdust lane (12 m south of the site's middle, clear of the flags, the horns and the wrestling ring) → a prompt "Put the stone (3 of 3 today)" and a box round the stone; E (keyboard) / Y (pad) takes it. Does the stone read as a stone, the lane as a lane, in Cartoon, PS1 and Realistic?
- [ ] Hold LMB / RB, watch the meter rise and fall, let go on the peak → the effort sound; is the meter easy to read, the peak band clear, a heavier stone slower? Then press LMB / RB on each of the four drum beats → PERFECT / GOOD / MISS flashes, a drum you can follow. Does it feel fair at 90 BPM with a pad and with the mouse?
- [ ] Look up to about 40 degrees at the fourth beat (the push) → the stone flies its arc and lands; the distance, Batzen and points show beside the body, a stake with your name and the distance stands 90 s. Does a perfect put near 4 m feel right for the real Unspunnen stone, and how much further does the 40 kg and the 18 kg one go (Space while lifting changes the stone)?
- [ ] Let go of the lift at the bottom of the meter → "The stone slipped from your hands", nothing paid, it still counts as a put. Walk, jump, a held weapon and the hotbar do nothing during an attempt; E again puts the stone down and gives them back.
- [ ] Three puts, then the prompt says the stone rests until tomorrow; the Journal's Festival tab shows the Batzen and the best score.
- [ ] Two players (or a second client) near the stone → the other sees your stone fly and the stake with your name; pay and score are yours alone, and the party board gains your points.
- [ ] Walking away from the stone mid-attempt (it is locked: try the game menu or Esc) → nothing breaks; after 90 s without a lift the attempt expires.
- [ ] VR headset (pad route): E at the stone is the grip / Y, the trigger is `use_item`; judge whether the meter and the lamps are readable in the headset (they sit beside the body). The real two-handed lift and push by hand speed is not built.
- [ ] Real map, dev region, `--date 2027-08-16`: the Älplerfest relocates to the region's largest town ("held this year in ..."), with the poles, the stone and the lane at its middle.
## Älplerfest flag swinging, part 5 of the festivals ([#1316](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1316))

The checks drive the stick and the use key from a script; what only a person can judge is the feel of the figures and the screen. Run with the Älplerfest on (a real date 17-24 August, or the operator's `occasions.json`, or `--festival test --date 2027-08-16`).
- [ ] Mouse: at the flag stage press the interact key → "Getting in line", a drum, the ring closes on each beat; draw the figure with the mouse (up, a circle, an eight, a sideways sweep, down) → it is recognised without big sweeps, the camera does not turn, the flag in your hand follows the figure.
- [ ] Pad: the same with the right stick (the stick's feel at 30 Hz sampling, the dead zone) and the use button for the throw.
- [ ] VR headset (pad route): Y at the stage, the right stick draws the figures, the trigger throws; the HUD is readable at the bottom of the view, not in the way. Swinging the flag by hand is not built.
- [ ] The high throw: hold, let go on the 1st, 2nd or 3rd beat → the flag flies as many beats as it was held plus one; catching on the beat it lands is fair at 96 BPM; a dropped flag ends the routine and the score.
- [ ] Two people ask within a few seconds of each other → the same routine, "in step x1.3" when both land most figures; one alone gets no bonus.
- [ ] Cartoon, PS1 and Realistic: the flag's cloth and the stage look right in each (the cloth is its own shader, it ignores the style).
- [ ] The result panel and the Journal's Festival tab agree on the points and the Batzen; the daily cap of 100 Batzen stops the Batzen but not the points.
- [ ] Real map: the Älplerfest at Interlaken's Höhematte (a western-region run moves it to the largest town); the stage stands on level ground there.

## Désalpe, the cow drive, part 7 of the festivals ([#1341](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1341))

The checks script the keys and the walk behind the herd; what only a person can judge is how it looks and feels. `--festival test --date 2027-09-24 --world fixture --chunks fixture:flat` puts the Désalpe at the fixture start (a 45 m route); on a real date (20 September to 5 October) at Charmey (a western-region run moves it to the largest town) the route is 700 m.

- [ ] On the alp, walk up to a cow: the prompt says "Dress the cow: wreath" with the outline round it; E (pad Y) turns the shape (wreath, straw hat, fan crown, bare), LMB (pad X) turns the colour. Do the three headdresses read from 5 m in Cartoon, PS1 and Realistic, and do the six colours differ? Does the Leitkuh's big Treichel look right on every cow's neck (dressed and bare)?
- [ ] Look at the dressed herd from the side, front and a distance of 60 m: headdress on the head when the head is down grazing and up walking; the Treichel swings with the stride. Any flicker (z-fight) on the headdresses or the collar?
- [ ] At the alp sign: E (pad Y) starts the Alpabzug, the herder's call plays. Walk behind the herd: do the cows stay together, go on when you are behind them and back off when you come at them from the front? Is the 5 m flight zone right on foot and the 9 m mounted (bike, quad, tractor)? Do the cows stop when you leave them?
- [ ] At a junction a cow or two wander into the field: does the panel say so ("wandered off: go round them from the field side"), and does it take the right feel to bring them back? Are they lost (150 m) too easily?
- [ ] Alone, farmhands make up the flanks: do they read as help (not as cheating) and is the herd still lost cows to a careless player? With a bot beside you?
- [ ] Sounds (sound player: Treichel big / middle / small, herder call): the bells ring from the walking cows near you, deeper on the Leitkuh, the call at the start. Level against the field herds' moos and the alp ambience.
- [ ] At the fair the herd stands in the ring (the round fence, the board of rosettes). On day 4 (`--date 2027-09-24`) walk into the ring: E (pad Y) gives the vote to the herd shown, LMB (pad X) picks the next one; your own herd is refused. Online with a friend: they vote for yours, you see the count.
- [ ] The result panel, the chat line and the Journal's Festival tab agree on the points and the Batzen; the daily cap of 120 stops the Batzen but not the points.
- [ ] VR (pad route): Y at a cow, the sign and the ring, the trigger for the colour and the pick; the panel readable at the bottom of the view. Dressing by holding a headdress to the cow's head is not built.
- [ ] Real map: the Désalpe at Charmey's fair: the alp (700 m up the valley, in the seeded direction) and the fair's ring stand on level ground and the herd does not walk through houses or off a cliff (the route is not the real road yet).

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

## Pinball ([#1333](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1333))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2510425,1140985` (BowlinGland, Gland: its main door reads as locked, go in by the side door; two pinballs by the arcade corner). Any arcade or bar with pinballs works.

- [ ] E at a machine's front → the eye view over the lockdown bar, the backbox's display at the top; a game starts (the ball on the plunger). V → straight down; V → back.
- [ ] Hold Space and let go → the ball goes up the shooter lane; soft → into the top lanes (the lit one: "SKILL SHOT"); hard → round the arch.
- [ ] Left Shift flips only the left flipper, Right Shift only the right; Enter pulls the plunger and does not open the chat (Enter away from a machine still does).
- [ ] Finish a game with a good score → "New high score: #n!", then in attract the backbox shows the high scores with your name, in the table's style (reels, segments, dots, LCD). (A server restart keeping them is in `tools/pinballnetcheck.sh`.)
- [ ] A / D (and the arrows) flip; a ball rolling down an inlane can be cradled and aimed. Do the flippers feel strong enough, too strong? Can you make each ramp and orbit on purpose?
- [ ] W nudges (Q / R to the sides): a few hard nudges → "DANGER", then TILT (flippers dead, the ball drains, no bonus).
- [ ] Play a whole game on each of the four tables (Clockwork, Space Mission, Haunted Castle, Alpine Ski Race): the lit scoop starts a mission, its shots blink, the timer runs; lock lights, two locks → multiball; ball save; the bonus at the drain; GAME OVER and MATCH. Is it fun? Is any shot impossible or too easy?
- [ ] The backbox per era readable from the player's eye and from 3 m away: reels (Clockwork), segments (Space), orange dots (Castle), LCD (Alpine).
- [ ] Sounds: flippers, bumpers, slings, drops, spinner, plunger, drain, knocker; Clockwork's chimes; the jingles on the others; the ball's roll and the wire forms.
- [ ] Pad: LB / RB and LT / RT flip, A holds the plunger (and the right stick pulled back), X nudges (left stick for the side), R3 the top view, Y Start, B steps back.
- [ ] VR: reach out and grip at the machine; the head stays the view; the grips and triggers flip, A plunges, X nudges with a buzz.
- [ ] Two players online in the same venue: one plays, the other watches the balls, flippers, lamps and backbox move a little behind; the watcher presses E at the machine during ball 1 → joins as player 2; at the drain the table passes to them (their client runs it), scores on the backbox for both.
- [ ] The player up walks out of the venue mid-ball → their ball is skipped, the next player is up (or GAME OVER alone).
## Venue patrons and ambience ([#1331](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1331))

`--enter bowlingland --time 20` (BowlinGland, Gland), then `/enter "pathé galeries"`, `/enter "brasserie de montelly"`, `/enter hype club` (add `--patronhour 1` for the club, `12.5` for lunch). Walk about in each.

- [ ] Look away from a full table and back, many times over a few minutes → guests change only while you were not looking; nobody pops in or out on screen.
- [ ] Sit in a cinema seat a patron is in → the patron is gone as you sit; stand up and leave the hall, wait 15 minutes of play, come back → the audience may have changed, never while you were in it.
- [ ] A queue at the kebab or the box office → it steps forward now and then, the served customer steps aside; nobody joins the back while you watch it.
- [ ] The loops look right at a glance: eating, sipping, talking, the pool shot, darts, table football, the arcade sticks, the dancers, the cheers on the settees (no arm through a table or a head).
- [ ] Sound: the murmur and the cutlery sit right under the venue's music, louder in a full room, gone in an empty one; the grinder, the tap, the jingles, the coin, the pool click, the dart and the cheer are not too loud or too often.
- [ ] A terrace at lunch on a warm day (`/weather temp 20`) → most tables taken; at 23:00 or in snow → empty.
- [ ] Perf on the Quest: a full multiplex hall and a full restaurant → note the frame time.

## Bowling ([#1328](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1328))

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2510425,1140985 --time 22` (BowlinGland, Gland; its main door reads as locked, go in by the side door on the same wall, 34 m along).

- [ ] E at the shoe counter → signed in, the hire shoes on your feet (red and cream, blue heel); E again → shoes back.
- [ ] E at a ball rack → the ball you face is in your hand (the lower row when looking down), its weight in the prompt.
- [ ] E on your lane's approach → the bowling camera: A/D moves your spot, Space sets it, A/D turns the line, Space, hold Space and move the mouse sideways → the curve bends; let go → the ball rolls at once, the pins fall, the slow-motion replay plays on the screen, then the score. Does the hook feel right (too much, too little)? Does the swing's power feel controllable?
- [ ] Shift in the bowling camera → the view down at the pins.
- [ ] The ball comes back to the hood with a thunk after a few seconds; E at the hood → it is in your hand again.
- [ ] Sound: the ball's rumble rising with speed, the crash, the machine, the gutter's rattle.
- [ ] Pad: Y at the counter, rack, hood and approach; left stick and A for the steps, L3 to look at the pins, B steps back.
- [ ] VR: grip at the counter and the rack; on the approach grip your ball, swing and let go → it rolls with your line and speed; twisting the wrist at the release hooks it.
- [ ] Two players online on one lane → turns alternate; the other sees your ball roll and the same pins fall; a third who walks in later sees the score and the standing pins.
## Älplerfest alphorn and yodel ([#1317](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1317))

The Älplerfest runs 14-24 August (Interlaken; a region without it moves it to its largest town). Try it any day with the check-only festival: `<godot> --path . -- --festival test --date 2027-03-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure`, or with the real map and `--date 2027-08-18` near Interlaken or the relocated town.

- [ ] Walk to the three horns on trestles, 16 m east and 7 m north of the festival's middle → the prompt "Play the alphorn and yodel" appears; E starts a four-beat count-in. Judge the look of the horns (Cartoon, PS1, Realistic) and whether the row is easy to find.
- [ ] Play the melody with the mouse (wheel to pick the note, hold left button from a note's start to its end) → the word PERFECT / GOOD / MISS shows on each press, the breath bar drains while blowing and refills in rests, the note sounds like an alphorn. Is the bottom panel readable at 1080p and with `--mobile`, and does it stay clear of the hotbar?
- [ ] Same on a pad (R stick up / down to pick, RT or RB to blow, A for the yodel) and in a headset (right stick, trigger, A through the pad route) → the same song plays; the prompts name the right buttons. VR gap: raising a horn to the mouth is not built.
- [ ] With the real map in a wide valley (Interlaken), blow facing a mountain face → the note comes back a few seconds later from the wall, duller and quieter; facing the open lake or a flat plain → no echo. Does the delay feel like the distance?
- [ ] Two or three players sit down within 45 s of each other (the first opens the song, the others join it) → they get melody, second voice and drone, play the same song, and each is told the harmony multiplier (up to x1.6) and the Batzen; the Journal's Festival tab and the party board show the points. Does the harmony reward feel fair? Does a late joiner understand they play the same chart?
- [ ] Press Esc or walk away mid-song → the song ends with nothing paid and a message; a fifth player is told the row is full.
- [ ] Others near the row do not hear the blower (a known gap): is that too strange for a festival? A bot taking a seat is a server-side call; there is no bot spawner for it in the game yet.

## Errands part 2: world events, the hiker and postcard cards ([#1340](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1340))

A windowed offline game on real terrain with the adventure on. `--worldevent cow,hiker,avalanche,fire` plans one of each in turn near you (season and chance aside); `--world-events rare|off` changes how often they come on their own. A fire needs forest cover under you, an avalanche steep ground (the forced ones relax the height and the slope).

- [ ] The cow: a Simmental cow stands 45 m off and walks away from its pasture; stand on its far side within 7 m and it turns home at a walking pace. Does it read as a stray cow, and does the bell clank carry?
- [ ] The lost hiker: a compass diamond ("Last seen near ...") and a map letter H; find them (a figure standing by a peak or pass), E / Y talks, they follow you to the hut or stop. Does the walking figure look right behind you, and the call carry?
- [ ] The avalanche: a heap of snow blocks on a slope; within 60 m a beep quickens as you near ("Signal 25 m"), hold G (pad X) at the spot: the bar fills in about eight seconds, a probe pole marks the place. Is the cone well set into the slope (no floating or buried rim)?
- [ ] The fire: flames and rising smoke at a forest edge; beat a cell out by holding G (pad X) next to it for two seconds; the smoke dims the view up close but never hurts. Do the flames flicker well, is the smoke too heavy or too light, does it spread too fast (one cell in 15 s)?
- [ ] Two players: both see the same event, dig it out in half the time, and both are paid (a toast "CHF 60, +5 VS"); five of one kind give a title in the Journal.
- [ ] Walk into a Versteckis game or a Battle Royale round near an event: no toast, no pay, nothing drawn from it.
- [ ] VR: the dig and the beating on the gather hold (X); grip-and-pull is not built, so the hold stays on the button.
- [ ] The lost hiker's Postkarte at a hut warden ("A hiker is lost near ..."): the hiker sits where the card says, talking is step 1, the walk to the hut step 2, the card pays; the postcard home ("A postcard of ...", take the photo of the famous view then post the card at a post office; a view already in the album still counts).
- [ ] The Errands tab lists the cards near each party member; the Atlas shows "Errands: Bekannt (12 points)" under a canton heading.

## Teleport on foot lands outside buildings ([#1342](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1342))

- [ ] On foot, click a house in a dense town on the adventure map (and the XR hand map, `/goto` onto a footprint) → you land on the nearest open ground beside it, never inside; the travel curtain lifts only once you stand there.
- [ ] Teleport into a forest → you end up in the nearest clearing (up to 150 m); is that acceptable?

## City main-thread hot spots ([#1345](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1345))

- [ ] Walk up to a stadium during a show (`--concert 1`, or a venue with an event today): the rig, the LED wall and the crowd are lit and moving as you cross 900 m, with no frozen beams or black screens on the first frames, and the beams swing in from their DMX position rather than from a stale one.
- [ ] Walk out beyond 900 m and back, once while the show is on and once after it has ended: the crowd is gone when the show is off, and the rig comes back lit.
- [ ] Stand beside a building-site crane (a tower crane at rest, then one worked from its remote): the jib, trolley, hook and rope still move, and a crane at rest stands still with its hook where it was.
- [ ] Walk along a street with doors: the "Open the door" prompt appears at the right doors, facing one door over a nearer one at your back, and in the lower right the key hints (E, R, F1) still change when you step to a door, mount a vehicle or touch a pad.

## Playtest panel confirmations ([#1388](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1388))

`tools/playtest.sh batch` (or `--playtest --playtest-batch --playtest-record`).

- [ ] Pad: L3+R3 opens the panel, Note → "✔ Note N saved" beside "Recording · N notes" for 4 s, and on the card once the panel is closed. End session → "Saving the session…", then "Not recording: session ended", the report's path and "Claude reads it next"; Note and End session go grey and the focus lands on Top view, not nowhere.
- [ ] VR: the same Note and End session on the XrUi panel through XrPad: the confirmations are readable at the panel's distance.
- [ ] With the movie on (no `--playtest-nomovie`): End session waits on "Saving…" while the replay is saved, then the "Saved" line names the 3D replay.

## Montreux Jazz Festival (PR for [#1394](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1394))

- [ ] With the real map at Montreux (the full region needed; `--date 2027-07-11 --time 21` shows the stage today, or in the real 3-18 July window): the small stage stands on the Place du Marché by the quay, facing east with the lake behind the band, the crowd seated on the sand, the promenade along the water free to walk, the fence open on the lake side, nothing inside the stage. Does it look like Montreux? Outside the festival's dates the square is the square (no stage, no crowd, no fence).
- [ ] The quartet (an upright bass, a sax, a piano with its stool, a drum kit): do the four instruments look right in Cartoon, PS1 and Realistic? Does the sway read as jazz, and do the warm washes feel calm (never a strobe or a flash)?
- [ ] Listening: E at the stage-front rail (pad Y, VR grip or the pad's Y) marks you; a toast top right for each song heard to its end. Does the toast stay clear of the stage and fade in about 3 s? Is a song's 6 Batzen (about CHF 2 a minute at the festival's real pace) a fair pay?
- [ ] The encore, alone and with a friend on a second machine: the ring closes at the bottom centre on every beat, the meter's text ("7 of 12") is readable, jump / the pad's A / the VR pad's A claps. Does the ring feel fair with a pad and in a headset? Two friends with 6 claps each fill the meter; does it feel good that strangers' claps count?
- [ ] The Jazz Night (day 8) and the closing night (day 16, `--festival test` is not needed: set the date `--date 2027-07-17` at the real map): the set is longer, the last song of the closing night pays the 20 Batzen finale and the two confetti cannons fire on each song's last chorus.
- [ ] A real 3-18 July run: the festival is on, the Journal's Festival tab shows the days, the Jazz pass (item 511) is in the pack after the first song, the Batzen convert after the last day.
- [ ] No audio plays at the stage yet (lights only, the made-up songs): once the owner picks copyright-free jazz tracks (`CdLibrary.JazzUrls`), does the set follow the CD, and does the 80 % rule need `ConcertMusic.At` as the games' clock (a follow-up noted in `occasions/festival-montreuxjazz`)?

## What's new (PR for [#1413](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1413))

- [ ] In a real exported build, update from an older release (delta patch, then again by unpacking the archive by hand) and reach the title: the "What's new" popup lists every release since, newest first, once; the next launch shows none. Offline at the first launch after an update: no popup, it comes at the next online launch.
- [ ] Pad and VR: the "What's new" title entry opens the notes; the stick scrolls them, B closes (VR: pointer on Close). Is the text readable at the panel's distance?

## The real sun and moon (PR [#1436](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1436), [#1430](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1430) H6)

Normal play (no check flags), any region; `--date` pins the day.

- [ ] Play an evening today: the sun sets around the time a weather app gives for your town, in the south-west in winter, the north-west in summer; `/time set sunset` goes there.
- [ ] Night with the moon up: hills keep a lit side facing the moon, its glow sits where the real moon is; a night with no moon (around a new moon) still reads, not flat.
- [ ] `--date 2026-12-24`: Christmas snow under a short day, dusk before 17:00; a paraglider finds weaker thermals than in June.
- [ ] Online, a client joining a server: both see the same sun.

## Earth curvature drawn round the camera (PR for [#187](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/187))

Launch: `--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2548600,1148500` (Lavaux, the Alps across the lake).

- [ ] PC VR (Quest via Link): look at the far side of the lake and the Alps → both eyes agree, nothing swims or doubles at the horizon as you turn your head.
- [ ] Standalone Quest: the same view → the horizon and the far peaks sit where they do on PC, no seam or gap under the far ridges.
- [ ] Compare a known panorama with a photo (Lavaux → Dents du Midi, or La Dôle → the Alps) → the far peaks stand as high above the lake as in the photo, no longer ~170 m too tall at 50 km.
- [ ] Debug menu → Terrain → "Earth curvature" off and on while looking at the far horizon → the far ground drops and rises; nearby nothing moves.
- [ ] Fly the Cessna across several km (origin shifts) looking at the horizon → no jump in the horizon at a shift.
- [ ] Navigate to a place 20-30 km away (adventure map) → the diamond sits on the place, not tens of metres above it.

## Telescope and the moon in the sky (PR [#1441](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1441), [#1440](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1440))

`/spawn telescope`, any region, a night with the moon up (`--date 2026-12-23 --time 23` for a full moon).

- [ ] The moon in the night sky with the naked eye: a small bright disc where the moon really is (a sky app), the right phase; by day a pale disc, its dark side the sky's blue.
- [ ] Set the telescope up (Use), E at it: the view opens, the wheel zooms, the mouse turns it slowly; LMB locks on the moon and holds it while the hours pass; E steps back.
- [ ] Pad: D-pad ↑ ↓ zoom, right stick turns, RT locks, B steps back.
- [ ] VR: Y or grip at the tripod; the eyepiece's picture floats before the eyes and follows the head; the right stick zooms, the trigger locks.
- [ ] Pointed at far mountains at 8,000 mm: the frame rate holds (the zoom-aware detail has no cap yet).
- [ ] Online: a second player at the tripod reads "Someone is looking through the telescope" and gets in once you step back.

## The night sky: stars, planets, satellites (PR [#1445](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1445), [#1444](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1444))

Normal play, a clear night (`--occasion none` in December: the snowfall hides the sky).

- [ ] The stars against a sky app tonight: Orion, the Plough, Cassiopeia where they are; they come out at dusk and sink into the haze at the horizon.
- [ ] Venus, Jupiter, Saturn, Mars as bright points where a sky app has them; Venus also by day.
- [ ] The telescope on Jupiter (lock with LMB) at 20,000 mm: its belts, its four moons beside it; Saturn's rings and Titan.
- [ ] After dusk or before dawn, satellites drift across as points; the ISS on a pass (Heavens-Above times for your town) where and when it should be. Online first: the game fetches fresh elements once a day (`[satellites] fetched` in the log).
- [ ] The frame rate with the night sky up, and with Starlink's ten thousand after the first fetch.

## The curvature at altitude (PR [#1458](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1458), [#1430](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1430) H1)

`--chunks D:/UnitSportSwitzerland/terrain_chunks --at 2538300,1151200 --occasion none`, the spectator (T), boost up.

- [ ] Climb steadily from the street to 50 km: the ground falls away smoothly, no jump or shimmer at 5, 20 or 50 km; it stops at 50 km.
- [ ] The UFO the same way: it stops climbing at 50 km.
- [ ] VR: the same climb, both eyes agree on the horizon all the way.
- [ ] From 20-50 km: no z-fighting on the ground (the far plane grows to ~1,000 km), frame rate as on the ground.

## The sky's depth floor and the shuttle film (PR [#1519](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1519), [#1518](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1518))

- [ ] In space (`/orbit 100`, or the shuttle's drive at 60-130 km), look straight at a planet, the moon and the shuttle: no disc of missing stars round what you look at.
- [ ] On the ground at night (`--sun real`, a clear night), stars over a mountain range 50-70 km away: they still stop at the ridge line (stars now win against ridges within ~2 depth steps of the far plane, past ~62 km of a 70 km view).
- [ ] At noon only Venus shows by day; Jupiter and Mercury come out with the stars at dusk.
- [ ] `tools/shuttlefilm.sh` on another machine: the film has the game's sound in step (the take-off's roar with the take-off).

## From space to the ground (PR [#1529](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1529), [#1528](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1528))

- [ ] Climb (or come down from `/orbit 100`) over Switzerland between 5 and 80 km: no patch of tiles with a hard edge against the globe; the flat world's edge dissolves into it.
- [ ] Land from space at Geneva or Zurich: from 15 km down the ground and the Alps show out to the horizon, no haze-coloured plane with lakes on it.
- [ ] The same in VR (a standalone headset sinks the faded edge instead of discarding it).
- [ ] From orbit and on the way down, the atmosphere's haze and limb glow cover the whole view (no clear disc round where you look).

## No snow above the clouds (PR [#1641](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1641), [#1637](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1637))

- [ ] `--occasion christmas`, take off in a plane and climb: the snow thins out about 2 km over the valley floor and is gone above it, no pop; coming back down, it returns.
- [ ] Inside a house with a door open: the street's snow still shows through the doorway.

## Space under `--frame earth` (PR [#1638](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1638), [#1630](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1630), [#1631](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1631))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500956,1117569 --date 2026-06-21 --time 11`, in a window; beside it the same without `--frame`/`--leaves`.

- [ ] Fly the spectator straight up to 400 km and back down, by keyboard, then by pad → the sky goes black past ~60 km (`[origin] the earth frame re-anchors every 20 km from 60.x km`), the limb, the Alps and the Mediterranean as in the flat run; back below 50 km the 3 km re-anchors resume, and on the ground the town is there at once.
- [ ] The UFO, `/orbit 400`, a few minutes, then thrust down and land → the orbit looks as on flat, the ground loads under the landing.
- [ ] The shuttle: the `--shuttlefilm` command line of `tools/shuttlefilm.sh` plus `--frame earth --leaves ...` → the climb to black and the landing on 22 look as the flat film's.

## The cloud deck while it snows (PR [#1644](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1644), [#1643](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1643))

- [ ] `--occasion christmas`, fly a plane up through the deck: grey overcast under it, a white-out inside, a sea of cloud under you above it; frame rate unchanged.
- [ ] Over a lake from above the deck, and under it: no water surface drawn over the cloud or through it.
- [ ] In VR: the deck is the same in both eyes.

## Traffic on leaves under `--frame earth` (PR [#1645](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1645), [#1636](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1636))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2500956,1117569 --walkers 80 --bikes 40 --trams 6`, in a window, against the same without `--frame`/`--leaves`. The graphs take ~40 s to build at first (`[traffic] built around`).

- [ ] Drive a car through Geneva's centre for 5 km, across two re-anchors and several graph rebuilds (every 800 m) → cars, trams, walkers and riders much as dense as in flat; cars stop at red and go on green; no car, tram or walker jumps, turns round on the spot or vanishes at a rebuild or a re-anchor.
- [ ] Stand at the Pont du Mont-Blanc lights for two cycles → walkers cross on green only, riders wait at their line, trams run their lines over the bridge.

## The traffic-lights region under `--frame earth` (PR [#1651](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1651), [#1647](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1647))

With "World frame" set to earth (or `--frame earth`): Play solo, World: Traffic lights region.

- [ ] Start it the first time → the loading screen says the region and its leaves are being built (up to two minutes), then you stand at J1's south-west corner on flat grass.
- [ ] Tab to J2, J12-GE and J14 and watch a cycle at each → lights, poles, paint and traffic as on flat. Where a leaf edge crosses an arm, a pocket or a bike lane may be missing ([#1660](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1660)).
- [ ] Host a game in that world and join from a second copy → the host builds the leaves before the server starts, and both stand on the same junctions.

## Leaves streamed from the server under `--frame earth` (PR [#1662](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1662), [#1655](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1655))

A server `--server --frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube`; a client in a window `--connect <host> --frame earth --leaves <an empty folder> --cache <an empty folder> --at 2500956,1117569` (Geneva).

- [ ] Join and look round while the ground arrives → the fill (if on) gives way to the real ground in place, no hole, no fall; roads, buildings and the lake come with it.
- [ ] Drive 5 km out of town at speed → the ground keeps up (a few seconds' wait at most at the 3 MB/s cap), the far view under the globe fills in.
- [ ] Settings → Data: the cache grows, Clear empties it, the session's MB count rises; Low data on → near ground still comes, the far view past it stays coarser.
- [ ] On the phone over mobile data, the metered warning, then the same join → ground arrives; note the MB on arrival.

## The earth fill on the globe's pyramid (PR [#1688](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1688), [#1678](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1678))

`--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks --at 2546600,1148700` (Lavaux, by B4's staircase at E ≈ 2547), in a window, against the same without `--frame`/`--leaves`.

- [ ] Walk, then drive east along the shore road from the real leaves into the fill → no hole, step or trench where the real ground ends; the lake runs on as one water, never a grey band or green land on it.
- [ ] Fly at 300 m along the shore from Cully to Vevey, then from 12 km up over the staircase → one shoreline, no squares, seams or pale lines on the water.
- [ ] The same in PS1 and Realistic (`/style`) → as in Cartoon (only Cartoon was shot).
- [ ] Look for the fill's generated roads near the shore → none runs under the lake's water.

## Group rides on the home trainer, and the trainer itself ([#1467](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1467))

Two PCs (or two windows) on one server, Cartoon, `--date 2027-07-15 --time 12`; the owner's smart trainer on one of them.

- [ ] Pair the real trainer (Training app → Search, then the trainer), load a GPX, ride 2 km → watts and cadence in the HUD, the resistance felt changing on a climb (SIM), ERG holding its target: the first run of RideLink's WinRT path on real FTMS hardware.
- [ ] On the trainer's PC: Training app → Host a group ride; on the other: Training app → Join → both on the same course, each sees the other's bike on the road and "Group:" in the HUD with both names and the gap in metres.
- [ ] Ride close behind the other rider on a flat → the speed rises a little at the same watts (the draft).
- [ ] Join from a pad and in VR (the app's buttons by stick and by poke) → the same as with the mouse.
- [ ] One rider stops the training → it leaves the group; the other's HUD shows only itself; when it stops too, the group is gone from the list.

 ([#1467](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1467))

In a window, Cartoon, `--date 2027-07-15 --time 12`, the phone's Training app, a GPX through a town (or `--trainingcheck traffic --trainer-sim --world fixture --chunks fixture:junction --traffic 60` windowed).

- [ ] Ride into a queue at a junction or a red light → the bike rides on through the queued cars at its own speed, no jolt, no stop; judge whether passing through a car looks acceptable from the chase camera and in first person.
- [ ] A car stopped at the road's edge on a wide road → the bike steps round it rather than through it.
- [ ] After the finish, ride the bike yourself into a parked traffic car → it is solid again.

## Farm fields, Christmas gifts and the trainer under `--frame earth` (PR [#1730](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1730), [#1729](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1729))

Offline, in a window, each against the same without `--frame earth`.

- [ ] `--frame earth --farmmonth 7` on the default fixture course, walk to the wheat field and round it → the crops lie on the ground (never floating or sunk), their edges follow the field, as on the flat world.
- [ ] `--frame earth --date 2026-12-15` on the generated world, walk to a village's square → the Christmas tree with five gifts under it stands on the ground in the open; gather (G) a gift → it goes.
- [ ] `--frame earth --trainingcheck --trainer-sim --world fixture --chunks fixture:hairpin` → the rider rides round a car stopped at the edge where the road has room, and through the traffic otherwise (#1467): it never queues behind a car.

## No camera jump at the earth re-anchor (PR [#1780](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1780), [#1763](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1763))

Offline, in a window, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks` (Cartoon), the playtest's `earth-3-reanchor-drive`.

- [ ] Walk (third person) across a re-anchor (the `[origin] shift` line, every 3 km) → the camera holds its place over the shoulder: no jump up, no ease back down.
- [ ] Drive a car across one in chase view, then in cockpit view → no jump. The headless check measured 0.0 cm in the car before the fix too: if a jump remains while driving, note what was driven and from which view.

## The earth re-anchor's stall (PR [#1784](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1784), [#1764](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1764))

Offline, in a window, `--frame earth --leaves D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube --chunks D:/UnitSportSwitzerland/terrain_chunks` (Cartoon), the playtest's `earth-4-reanchor-flight`, traffic on.

- [ ] Fly and drive across Geneva's re-anchors (every 3 km, the `[origin] shift` line) → no lag spike you notice; the line reads well under the 117-203 ms of Nov 12 (headless on a loaded machine: 23-48 ms), traffic keeps driving on its roads after each.

## A view opened by E keeps the press (PR [#1815](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1815), [#1813](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1813))

Offline, in a window (any frame).

- [ ] Set a telescope up, walk to it, press E (keyboard), then pad Y, then VR grip → the view through it opens and stays; E again steps back.
- [ ] E at a pinball machine → you step up, no game starts (and no coin goes) until you press E again.
- [ ] E at a pool table or dartboard → you step up without sitting down; E again sits.
- [ ] Empty hand, point at a set-up telescope (Geneva, `--frame earth`), after looking through it with the tube tilted → the yellow "pack it up" halo sits on the tube and the legs as drawn, tilted with the tube; same on a flag, a campfire and a workbench.

## The sky from space (PR [#1855](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1855), [#1845](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1845))

In a window, `--frame earth --sun real --date 2027-12-22`, Cartoon, the spectator, `/orbit 400` or the speed up to 20,000 km.

- [ ] Over the night side at `--time 5`, look at the moon (a waning crescent) → a glow with its lit side bright, never a black disc in a ring; the same from the ground at night.
- [ ] From 20,000 km with dawn or dusk under the camera → the Earth's day side as bright as with noon under it; the twilight band where the sun sets, about as wide on both sides of the line.
- [ ] From 400 km over the night side before dawn → satellites as moving points above and below the horizontal, gone behind the Earth's limb.

## The globe's terminator and the spectator speed readout (PR [#1806](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1806), [#1798](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1798))

In a window, `--frame earth --chunks D:/UnitSportSwitzerland/terrain_chunks`, spectator camera (Shift+T).

- [ ] Climb above 500 km and fly at 100 km/s, then with boost, along the day/night line → the lit half stays put on the ground: no drift with the camera, no snap twice a second.
- [ ] Change the speed (wheel, `^` / `'` on a Swiss keyboard, RB / LB) → the speed shows bottom right under the key hints, clear of the F3 overlay at top right, and fades 1.5 s later.
- [ ] On a Swiss keyboard the hints read "Wheel ↑ / ^" and "Wheel ↓ / '"; on a US keyboard "=" and "-".
- [ ] On a phone (`--mobile`) the speed shows at top centre, not under the pedals.
- [ ] At night in Geneva (`--date 2026-11-13`, after sunset) → no dim blue blob in the sky; the crescent moon low in the south-west, its dark part faintly lit, no star through it.
- [ ] Fly up over the night side as the sun sets below (1,000 km) → the sun shines above the Earth's limb; turn so the Earth hides it → it is gone. From the ground at night it never shows.
- [ ] With the telescope on the moon → the moon at its true size, as before.

## The sun as a body (PR [#1834](https://github.com/SuperQuentin/UnitSportSwitzerland/pull/1834), [#1479](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1479))

Offline, in a window, `--frame earth` in Geneva (Cartoon), `--date 2027-07-15`.

- [ ] Noon (`--time 13`) → a small bright disc with a soft corona and faint spikes, no 20° blob; stars and planets near it hidden.
- [ ] Sunset (`--time 20.6`, then 21) → an orange disc low over the Jura, a wider warm corona; it sets behind the ridge, cut by it.
- [ ] Walk so trees or buildings stand in front of the low sun → beams between them; Settings → Graphics → "Sun rays" off → gone, on → back.
- [ ] Night (`--time 23`) → no blob anywhere; the moon at its own place.
- [ ] Fly up past 40 km (spectator) → the sun stays where it was, gains strong spikes over black; behind the Earth it is gone.
- [ ] VR (Link): the sun and the beams in both eyes at the same place.

## Placed things after a restart (PR for [#1835](https://github.com/SuperQuentin/UnitSportSwitzerland/issues/1835))

- [ ] Offline (`--frame earth`, Geneva): set up a telescope, quit, start again, E at it → the view opens and stays.
- [ ] At the longest end (500,000 mm), follow the moon from the menu → its surface holds still; on the drive (Use) it may tremble by a pixel.
