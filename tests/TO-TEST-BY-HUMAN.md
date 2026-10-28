# To be tested by a human

What the headless checks could not verify: hands-on clicks, a real pad, a headset, a real-size region, a look or feel to judge.
Each PR that leaves something here adds it in the same change (`docs/notes/general/human-test-list.md`).
Once tested, delete the line, or note what was found (an issue link) and delete it once fixed.
Something to retest over and over belongs in the playtest suite instead (`tools/playtest.sh`, `general/playtest`).

Format: `- [ ] what to do → what should happen` under the feature's heading, with its PR.

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
- [ ] Finish a game with a good score → "New high score: #n!", then in attract the backbox shows the high scores with your name, in the table's style (reels, segments, dots, LCD); restart the server → still there.
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
