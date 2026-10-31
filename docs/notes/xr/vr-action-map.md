# VR action map: every player action, its VR way, and the coherence rules

Every action from `PlayerInput.RegisterActions` (plus the keys some screens read directly), what VR
does for it today and the target design. Status: **ok** works today, **gap** unreachable or wrong
in VR, **new** planned VR-native way. The work is tracked in #440 (prompts #435, fixes #436, hands #437 #438, extras #439).

## Design rules (check any new binding against them)

- **R1 Hands for physical, buttons for abstract.** Anything the player could reach (wheel, door,
  seat, ladder, lever, item, rock) is done by a **grip near it** (the `XrHands` pattern). Face
  buttons are for actions with no object (jump, crouch, engine).
- **R2 One meaning per control per context.** Triggers: on foot **R = use, L = aim**; mounted
  (car, plane, airliner, boat) **R = throttle / climb, L = brake / descend**. Never a third meaning.
- **R3 Right stick: snap turn on foot, a 4-way action pad when mounted** (the head is the look
  there): ↑ engine, → lights, ← roof / horn / couple / speedbrake, ↓ tune / fly camera.
- **R4 The body before a button when the body is natural** (crouch, ski lean, pole push, pigeon
  flap, wingsuit arms, look behind = turn your head), always with a button fallback for seated play.
- **R5 No action is unreachable.** Keyboard-only actions get a grabbable cab control, a poke button,
  the **wrist menu** or a radial wheel.
- **R6 Prompts name the control on the device in hand and in this context** (`InputHints`), never a
  typed key; in VR the names follow the detected controller (Quest names when unknown).

## On foot

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| move_* / sprint | WASD, L stick / Shift, L3 | ok: L stick head-relative, L3, or arc teleport (setting, #439) | room-scale walking |
| look / turn | mouse, R stick | ok: head + snap 15/30/45° or smooth (setting, #439) | keep |
| jump | Space / A | ok: A | keep |
| crouch_slide | Ctrl, C / B | ok: B, or crouch for real (#437) | keep |
| interact_mount | E / Y | ok: Y; grip a door, an item, a radio, or reach out and grip what E would act on (#437) | keep |
| concert pogo QTE (#1150): dancing at a concert, jump on the beat | Space / A | ok: the pad's A (`jump`); the ring is a CanvasLayer like the other HUDs | keep |
| sit on a stand's seat / stand up (#885) | E / Y on a stadium row; again, or walk off | ok: Y, or reach out and grip (`TryInteract(byHand)` sits before the dance); the stick stands up | keep |
| export or import a concert rig as MVR (#941) | the desk's MVR row (Export MVR, the file picker, Import, Own rig, Folder) or `/concert export`, `/concert import <file>` | ok: the desk is a CanvasLayer on the VR panel (`XrUi`), the ray clicks the picker and the buttons | keep |
| run a concert camera (#885) | `/concert camera take <n>` or the desk's Operate row; WASD / left stick along the rails, fly up/down rises, mouse / right stick looks, = - wheel / shoulders zoom, Enter / X cuts to it, Esc leaves | ok: the sticks and shoulders through `XrPad`; the viewfinder is a CanvasLayer, on the VR panel (`XrUi`) | new: a hand-held steadicam, gripped and aimed by the hand |
| a concert's light desk (#885) | E / Y at the front-of-house console | ok: Y, or reach out and grip (`TryInteract(byHand)`); the screen is a CanvasLayer, on the VR panel (`XrUi`), the ray clicks | keep |
| a cinema's booth: house lights (full, dim, off) at the panel's left half (#1238); the hall's projector panel at its right half (#1298: screen off, the slides, a video link) | E / Y at the half of the booth's panel you stand at; in the projector panel mouse, Tab/Enter, D-pad + A, B / Esc closes | ok: Y, or reach out and grip (`TryInteract(byHand)`): the same half rule; the projector panel is a CanvasLayer on the VR panel (`XrUi`), the laser clicks; **Paste** and the recent links mean no typing in a headset; **Share from this computer** (#1298 phase 3: monitor, window, camera, quality, PC sound, mic) is the same panel's pickers and button, by laser; a 3D film shows each eye its own half in the headset by itself (`cinema_screen`, `VIEW_INDEX`); the **Display** pickers (fit, ratio) by laser too | keep |
| pinball (#1325): step up to a machine and press Start (a game, or a player added in the first ball); flippers, plunger, nudge, the top view, step back | E / Y at the machine (and again for Start); A / D, the arrows or Left / Right Shift, pad LB / RB or LT / RT for the flippers; hold Space / S / Enter / pad A (or pull the right stick back) for the plunger; W / pad X to nudge, Q / R or the pad's left stick for its side; V / R3 the top view; Esc / B steps back | ok: reach out and grip at the machine (`TryInteract(byHand)`); at it the head stays the camera (real eye at the cabinet), the grips and triggers are the flipper buttons on the cabinet's sides (they are the pad's shoulders and triggers, `XrPad`), A the plunger, X a nudge with a buzz | new: the plunger pulled by the hand, a nudge from the hands shoving the cabinet |
| bowling (#1323): sign in at the shoe counter, take a ball at a rack or the hood, step up to bowl; then spot, line, swing (power, hook), look at the pins | E / Y at the counter, rack, hood or approach; A/D or left stick to move and turn, Space / A to set, hold Space / A to swing and move sideways for hook, let go to roll; Shift / L3 looks at the pins; Esc / B steps back | ok: reach out and grip (`TryInteract(byHand)`) at the counter, rack and hood; on the approach the grip closes on your ball, swing and open the hand to roll (`XrBowling`: the hand's speed and line, the wrist's twist for hook); or the same steps on the stick and A through `XrPad` | keep |
| radio on / off (#725) | tap E (pointed) or tap Use (held) / tap Y or the Use button; hold either for the panel | ok: poke the radio's red key with a controller tip (`XrHands.PokeRadio`); grip still opens the panel | keep |
| elevator call / floor list (#557) | E / Y at the call button or in the cabin | ok: Y, or grip the call button or the cabin's panel; the list is a pointable panel (`XrUi`) | keep |
| flat door, its lock (#557) | E / Y at the door | ok: Y, or grip the door; the dial on the stick | keep |
| public toilet doors: washrooms, stalls, WCs (#1285) | E / Y at the door (the flat door's action) | ok: Y, or grip the door | keep |
| ride_menu | R / Y with nothing near | ok: Y, wrist menu (#437) | keep |
| car_door / gather | G / X (tap / hold) | ok: X; doors by grip | gather = **grip and pull** the resource |
| emote_wheel | B / D-pad ↑ | ok: hold R stick ↑, aim with the right hand (#437) | keep |
| on-foot camera zoom (#1149): far out down to the face | Shift + wheel, = - / hold R3 + right stick up/down (a tap of R3 still switches the view) | n/a: VR is first person (#186), the head is the camera | keep |
| phone camera app: walk while it is open (#1149) | WASD, Space / left stick, A | ok: the body already walks with the phone out in VR (#920) | keep |
| phone (#889): out, quick action, open, back | ↑, ← → Enter, ↑, Esc ↓ / quick menu › Phone, D-pad A, D-pad ↑, B | ok (#920): grip at the left front pocket or wrist menu › Phone; the real index tip taps, drags and swipes the glass, or the right ray and trigger press from afar (`XrPhone`); the free hand swings it open, X opens / folds, B back / away; the arm aims the camera app, the holding trigger shoots, the zoom is the on-glass 1x 2x 5x chips (a trigger pulled off the phone does nothing since #1253), turned round (or the Selfie button) it is a selfie; raised to the eyes it travels (a blink after 3 s), to an ear it answers; let go it goes home to the pocket | keep |
| phone camera and photos (#1253): shutter, zoom, selfie, previous / next photo | click / Enter, wheel / Up Down / chips, F Tab right click (camera app only), Left Right, PgUp PgDn / RT, LB RB zoom, Y, LB RB page and previous / next | ok: trigger of the holding hand or the ray on the shutter; fingertip or ray on the 1x 2x 5x chips, the Selfie button, the ‹ › arrows; a sideways fingertip swipe is previous / next in the viewer | keep; pad A shoots when standing still is not done (no definition of standing still yet) |
| phone › Training app (#901): import, search, connect, start / stop | the phone's own navigation (Enter, Esc) / D-pad A B | ok: the phone's poke and ray, as every app (the OS file picker shows on the desktop) | keep |
| training: the next junction's road left / right (#901) | A D / left stick | ok: the stick, as steering; the Training app's Left / Right by poke | keep |
| training: ride_rejoin, ride_uturn, ride_erg, ride_power_up / down (#901) | PgUp PgDn \ ] [ / the Training app's buttons (no pad button is free on a bike) | ok: the Training app's Rejoin, Turn round, ERG, ±10 W by poke | a wrist-menu row if riders want it without the phone |
| sword swing, combo, charged, plunge (#1371) | LMB / RB | ok: R trigger (RB on foot) | **swing for real** (hand speed) |
| sword_dodge (#1371) | Shift / B | ok: B (`XrPad.RightB`) | a step or a dodge by body movement |
| sword_lock (#1371) | Middle button, Z / R3 | ok: R3 | grip-pointing at a target; the stamina bar has no VR display yet (**gap**: wrist display) |
| challenge / accept a fist fight (#495) | E / Y, looking at the player | ok: Y, or reach out and grip | keep |
| fight step / jump / crouch (#495) | A D, W Space, S Ctrl / L stick, A | ok: L stick towards / away, A, crouch for real (`XrPad.RealCrouch`) | room-scale stepping |
| fight_punch / fight_kick (#495) | LMB J, RMB K / X RB, Y LB | ok: R / L trigger | **punch and kick for real** (hand speed, R4) |
| fight_block (#495, hold) | Shift L / B | ok: B (`XrPad.RightB`, not the crouch's B) | forearms raised in front of the face |
| join the Älplerfest ring's queue (#1269) | E at the sawdust ring / Y | ok: the pad route through `XrPad` (Y at the ring); **gap**: gripping the ring's rope (after a headset playtest) | grip the rope |
| Schwingen lean / brace / throw (#1269) | move keys (towards / away / round), Shift (`fight_block`), LMB (`fight_punch`) throw, RMB (`fight_kick`) leg hook / L stick, B, X RB, Y LB | ok: L stick (forward = towards the rival), B (`XrPad.RightB`), R / L trigger | **gap**: gripping the belt and trouser leg for real, hand speed for the throw (after a headset playtest) |
| Schwingen Bodenarbeit (#1269, mash to pin) | LMB / RMB / X RB, Y LB | ok: the triggers | keep |
| aura_next (#907, timed on the downbeat; #914 the waiter's straight face) | Space, LMB / A, RB | ok: R trigger (sends RB on foot), or **strike the next move's pose for real** (`AuraBody.Classify` on `VrHands`, #914) | keep |
| aura planner: add / aura_remove / aura_lock / aura_suggest (#907) | click, arrows Enter / Backspace / F / G; A / X / Y / R3 | ok: stick + A / X / Y / R stick click | point at the cards with the laser |
| toggle_mode | T / D-pad ↓ | ok: wrist menu (#437) | keep |
| creative mode (#1002): toggle_mode, then jump twice to fly, jump / crouch up and down, boost | T, Space Space, Space / Shift, Ctrl / D-pad ↓, A A, A / B, L3 | ok: wrist menu, then the pad's A A, A / B, L3 | keep |
| spectator camera and back (#1002) | Shift+T / hold D-pad ↓ | ok: wrist menu "Spectator camera" | keep |
| climb ladder; free climbing (#1225) | W / S on a ladder; on a face: push W into it (in the air, or 0.25 s on the ground), then WASD / L stick | ok: stick as on a pad (seated play); rock faces and walls hand over hand (`XrClimb`, #439), drawing from the stamina meter (1 /s, 9 a metre) where it counts | keep |
| climb-jump / kick off / let go (#1225, on a face) | Space, S + Space, Ctrl / A, stick back + A, B | ok: the pad's A, stick back + A, B; by hand: a fast pull is the hand climb, push off with both hands and let go, open both hands | a fast pull > 2 m/s as the climb-jump's boost (not yet) |

## Items, building, inventory

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| use_item / aim_item | LMB, RMB / RB, LB | ok: R / L trigger | camera and binoculars **raised to the eye** = aim |
| drop_item | Q / — | ok: fling an empty squeeze, or the wrist menu (#437) | a real throw by release velocity |
| next / prev_item, slot_1-6 | Alt + wheel (the plain wheel zooms, #1369), 1-6 / D-pad → (tap) | ok: R stick ↓, a short grip at the right / left hip (#437) | slots shown at the hip |
| holster_item (#1369) | `,`, the number of the slot in hand / hold D-pad → | ok: hold the grip at the right hip (0.35 s, a buzz): puts the item away, again draws it (`XrHands.AtHip`, `Gesture`) | keep |
| inventory | I, Tab / Back (tap; opens on the release) | ok: Menu hold | keep |
| quick_menu (#840) | hold F2 / hold Back | ok: wrist menu Activities + Invites entries; a ring opened by Back is aimed by the right hand | a hold of Menu longer than the inventory's pulse |
| invite_decline (#840) | Shift+F2 / — | ok: wrist menu › Invites › Decline (laser), or Menu tap › Activities | keep |
| quick_wheel | X / D-pad ← | ok: hold a grip at the left hip, aim with the right hand, let go to pick (#489) | keep |
| build_turn, next piece | R, aim+wheel / D-pad ↑, LB+D-pad → | gap | **twist the hand** while aiming; R stick ←/→ steps the piece |
| fishing rod: cast, strike, reel, wind in (#493) | hold LMB + let go, LMB, hold LMB, RMB / the same on RB, LB | ok: R trigger (hold, let go: cast; press: strike; hold: reel), L trigger winds in | gap: **flick the rod hand** to cast (release speed = distance), **crank the reel** with the left hand (R1, R4) |
| bird_journal: the Journal (#1223: Quests, Birds, Fish and the Adventure plans' tabs; was the bird journal, #493) | J / quick menu › Journal, LB / RB tabs | ok: wrist menu › Journal, phone › Journal; tabs and buttons by pointer | keep |
| quest: track / start / abandon (#1223) | buttons in the Journal / A on the button | ok: laser + trigger on the button | keep |
| quest: hand over a delivery (#1223) | the Journal's Hand over button / A on it | ok: laser + trigger on the button | gap: `interact` (E / X) or a **grip** on the target's marker, once navigation draws markers (#1226) |
| place: look out from a viewpoint / use a stamp post (#1224) | `interact_mount` E / Y within 3 m of the cross, telescope or stamp post (`PoiMarkers`) | ok: reach out and grip it (`TryInteract(byHand)`) | keep |
| hold hands, rope up, let go (#1266) | hold E 0.5 s at a party member (tap stays the fight challenge), `/rope`; E with no target lets go | ok: hold Y; Y with no target | ok: the pad route through `XrPad` (hold Y); **gap**: gripping their hand (15 cm) and opening it to let go (`XrHandHold`) are not built |
| drink with alcohol (beer, wine, kirsch; #1271) | `use_item` with the drink in hand (the mouth pose) | RT | ok: the pad route (RT); **gap**: bringing the bottle to the mouth by hand |
| breathalyzer: blow into it for 5 s (#1271) | `use_item` with it in hand | RT | ok: the pad route (RT); **gap**: bringing it to the mouth by hand |
| breathalyzer: offer a test to a player (#1271) | `interact_mount` E at them with the electronic device in hand (shown by `InputHints`, outlined) | Y | ok: the pad route (Y) or a grip at them (`TryInteract(byHand)`) |
| fall into a hidden crevasse; climb the ice wall out; be hauled (#1372) | nothing to press: the fall happens; push W into the ice wall (free climb, as any face); the partner holds E 2 s | the stick into the wall (free climb); the partner holds Y | ok: the pad route and the hand climb (`XrClimb`: the wall is an ordinary static face); the partner's haul is the hold-Y route below; **gap**: pulling the rope hand over hand | keep |
| haul a roped partner (#1266) | hold E 2 s at the rope | hold Y | ok: the pad route (hold Y); **gap**: pulling the rope hand over hand |
| talk to an errand giver (#1267) | E / Y within 3 m of the figure (a prompt "Talk to ...") | ok: Y, or reach out and grip toward the giver (`TryInteract(byHand)`) | keep |
| errand dialogue: choose, next card, leave (#1267) | 1-3, Enter, Left / Right, Esc / D-pad, A, LB / RB, B | ok: laser + trigger on the panel, the pad's route | keep |
| hand a delivery over at a giver (#1267) | E / Y, then "Hand over" in the dialogue (the Journal's button still works) | ok: Y, or the laser on the choice; **closes the gap** of "hand over a delivery" for givers | keep |
| talk to a lost hiker (#1339) | E / Y within 3 m of the hiker (a prompt "Talk to the lost hiker"), then they follow | ok: Y, or reach out and grip (`TryInteract(byHand)`) | keep |
| dig out an avalanche burial, beat out a forest fire (#1339) | hold G / X within 3 m of the spot or the burning cell (the prompt "Hold to dig out the person" / "beat out the flames"; `gather` is the one hold) | ok: hold X | **grip and pull** the snow, like gather |
| Errands tab: Share with party, Join, Track (#1267) | J, then Q / E; click | ok: LB / RB for the tab, laser + trigger on the buttons | keep |
| leg-up, two-handle gate (#1266) | crouch at a wall / jump onto the crouched friend; hold E at a wheel | B / A; hold Y | real crouch (`XrPad.RealCrouch`) / A; the wheel by the pad route (hold Y); **gap**: gripping and turning it |
| sit at the pot, add, stir, dip, forfeit (#1266) | E at a table or hut; panel chips 1-4; hold LMB and trace an eight; LMB dip; 1-3 forfeit | Y; D-pad + A; right stick; RB; D-pad + A | ok: the panel by the ray, stir with the stick; **gap**: the real fork over the pot |
| open a chest (#1227) | `interact_mount` E / Y within 2.4 m (`RewardWorld`) | ok: reach out and grip it (`TryInteract(byHand)`) | **grip the lid and lift** (R1) |
| light a Höhenfeuer stack, ring a cowbell, wake a guide, start a trial, offer edelweiss (#1227) | E / Y at the stack, bell, burrow, post or offering box | ok: grip it; the offering's confirmation by the ray (`Ui/Modal`) | swing the bell's clapper (R1) |
| pick an edelweiss, add a cairn stone (#1227) | hold G / X (`gather`, offered before water and stone) | ok: hold X | **grip and pull**, like gather |
| ring run, pressure plate (#1227) | the body through the ring, standing on the plate | ok: the body (room-scale or stick) | keep |
| skip the chest's reveal (#1227) | any key / any button | ok: it fades by itself; a pad button through `XrPad` skips | keep |
| treasure detector ping (#1227) | Use (LMB / RB) with the detector in hand | ok: R trigger (`use_item`) | point it and squeeze |
| bottled gust (#1227) | Use (LMB / RB): thrown ahead | ok: R trigger (`use_item`) | a real throw by release velocity (the throw-drop target) |
| `/trial leave` (#1227) | chat | ok: wrist menu › chat | a quick menu entry (gap, also on pad) |
| Steinstossen (#1269): open an attempt at the Älplerfest's stone, change stone | E / Y at the stone (`interact_mount`), Space / A (`jump`) | new, pad route: the pad actions through `XrPad` (grip or Y at the stone), A; the prompt through `InputHints` | keep |
| Steinstossen: hold the lift, let go on the meter's peak, press on the drum's beats, push | hold LMB / RB (`use_item`), release, LMB on each beat; the camera's pitch is the angle | new, pad route: the trigger / shoulder (`use_item`), the head or stick sets the pitch; gap: a real two-handed lift and push by hand speed (plan section 3) | keep; the real lift is a later target |
| adventure map (#1224) | M (`teleport`) while adventuring / quick menu › Map; drag, wheel, click; left stick, LB / RB, A | ok: wrist menu › Map on the `XrUi` panel, ray drag and trigger (the shoulders do not zoom in VR) | keep |
| Atlas (#1224) | J then Q / E, LB / RB to the Atlas tab; buttons | ok: wrist menu › Journal, laser + trigger | keep |
| guide: the guiding gust (#1226) | N on foot / mounted (not in a bus, tipper, mixer, mini dumper: N is theirs) / quick menu › Guide | new: the open left hand held palm up 0.5 s (`XrRig.PalmUp`); wrist menu › Guide; a buzz on start | keep (headset check of the palm's sign) |
| departure board at a travel sign (#1226) | E / Y within 4 m (`interact_mount`) | ok: a grip at the sign (`PoiMarkers.TryInteract` → `TravelSigns`), Y | keep |
| pick and confirm a departure, buy a travelcard (#1226) | ↑ ↓ / type, Enter, Esc / D-pad, A, B | ok: laser + trigger on the row or button | keep |
| place / edit a map pin (#1226) | RMB on the map (1-6 icon, Enter) / X at the centre cross | ok: laser on the map + the side panel's "Pin the centre", ray picks the icon | keep |
| track a place, pin, travel point (#1226) | the card's Track / A on it | ok: laser + trigger on Track | keep |
| travel from the map or the Journal (#1226) | the card's / board's Travel / A | ok: laser + trigger | keep; the phone's map app does not list network points yet |
| compass (#1226) | the HUD strip (free roam) | new: the watch's bezel turns its N north, an amber notch toward the target, the distance on the dial; the target as a billboard, a tick within 100 m | keep (headset playtest: open question 6) |
| place card: Track / Show on map / Show in Atlas (#1224) | the card's buttons / A on them | ok: laser + trigger | keep |
| character_menu (the figure editor, #1127) | P on foot / quick menu › Character, Start › Character | ok: the quick menu ring aimed by the hand, the pause menu; the page by pointer | keep |
| server console (#1088): Multiplayer › a server's console button, or the refusal banner | click / A on the focused button; commands and messages are typed | ok: the menu panel and the laser; typing needs a keyboard (an admin tool, no pad or VR way to type) | keep |
| traffic tool: place / open a generator (#971) | LMB / RB with the tool in hand | ok: R trigger (`use_item`, no new action); the window by the laser | keep |
| farm by hand (#494) | LMB / RB with a hoe, seed or fertiliser in hand | ok: R trigger (`use_item`, no new action) | a hoe **swung down** at the ground |
| harvest a ripe field cell (#494) | hold G / X | ok: hold X (`gather`, the gathering hold) | **grip and pull** the crop, like gather |

## Mounted and driving

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| throttle / brake / steer | W S A D / RT LT stick | ok: triggers; **wheel by grip** | keep |
| tuck_boost / trick / boost | Shift F Q / X RB LB | ok: X, grips | keep |
| skis: stride (#899): a press of forward, never a hold; skates on the flat, steps off snow | tap W / tap RT or flick L stick ↑ | ok: a **pole push** (`XrRig.UpdateSki`, a pulse per push) | keep |
| ski lift (#912): board at a bottom station, on foot or on skis; let go of a drag lift | E / the pad's interact | ok: a **grip** reaching out at the station (`XrHands`, on skis too) | keep |
| skis: hockey stop (#899): brake + turn pivots the skis across | S + A/D / LT + stick | ok: L trigger + head lean | keep |
| skis and board: hop turn (#954): jump, then brake + turn in the air swings them across to stop | Space then S + A/D / A then LT + stick | ok: the pad's jump, then L trigger + head lean | keep |
| skis: snowplough (#933): brake alone opens a V; slower than 5 m/s brake + turn too | S / LT | ok: L trigger | keep |
| skis and board: schuss / straight-line (#933): hold, steering damped | Shift / X | ok: the **crouch** (`XrSession.SkiTuck`) | keep |
| snowboard (#922): carve toe / heel edge, tuck | A/D, Shift / stick, LB | ok: the ski **body lean** and crouch (`XrRig.UpdateSki`, `SnowRide.Is`) | keep |
| snowboard: push (#922): a press of forward, the free foot | tap W / tap RT | ok: the pad (no poles: the pole push is skis only) | keep |
| snowboard: heel-side skid stop (#922) | S / LT | ok: L trigger | keep |
| engine_toggle | Z / D-pad ↑ | ok: R stick ↑ | keep (R3) |
| e-bike assist level (#1167): engine_toggle steps Off, Eco, Tour, Sport, Turbo | Z / D-pad ↑ | ok: R stick ↑, or a **poke on the Bosch remote** inboard of the left grip (`XrCabControls` "ebike", placed from the drawn remote) | keep |
| lights_toggle | L / D-pad → | ok: R stick → (#436) | keep |
| cruise (speed regulator: car, truck, farm machine, motorbike, #494) | Y / hold D-pad → (a tap stays the lights) | ok: a **dash poke** in a car or truck (`XrCabControls`), or hold R stick → | keep |
| roof / horn / couple | O H / D-pad ← | ok: R stick ← (#436) | keep |
| tune | T / D-pad ↓ | ok: R stick ↓ (#436) | keep |
| radio next / prev / panel | U P R / — | ok: dash pokes (#438) | keep |
| take_wheel (passenger) | F / RB | ambiguous (free grip) | **grip the wheel** from the passenger seat |
| shift up / down, clutch | Shift Ctrl C / RB LB B | ok: grips, B | keep |
| couple a trailer (fifth wheel, drawbar, tow ball #463) | H / D-pad ← | ok: R stick ← (#436) | keep from the seat; **on foot, grip the coupler's latch** to couple / drop a trailer standing on the hitch |
| boat trailer: launch / winch the boat (#463) | G / X | ok: X (left controller) | + **grip and crank the winch handle** on the post |
| forklift mast up / down (#583, hold) | Shift Ctrl / RB LB | ok: grips, or the **mast lever** by hand (`XrCabControls` "forklift", `Kind.Hold`: pulled back the forks rise) | keep |
| roof_toggle as the telehandler's steering mode (#614: front, four-wheel, crab) | O / D-pad left | ok: a **dash button** (`XrCabControls` "telehandler", `Kind.Poke`) | keep |
| telehandler lift / extend / tilt (#614, hold, in work mode; arm_boom, shift_up/down and arm_bucket actions) | ↑ ↓, Shift Ctrl, ← → / R stick, RB LB | ok: the **boom's joystick** right of the seat as two levers (aft lifts, aft tilts back) and the **extend rocker** beside it (forward runs it out); work mode on the dash | keep |
| dig_mode as the roller's vibration (#614) | C / B | ok: the **red button on the console right of the wheel** (`XrCabControls` "roller", `Kind.Poke`) | keep |
| dig_mode (#611, excavator) | C / B | ok: B, or the **button on the left console** (`XrCabControls` "excavator", `Kind.Poke`) | keep |
| crane: climb the mast / take the remote / let go (#618) | E / Y at the ladder's foot or the remote's box | ok: Y, or grip there (as E) | **grip the rungs** hand over hand |
| crane: slew, trolley (#618, hold; `arm_slew_*`, `arm_stick_*`) | A D, W S / left stick | new: cab: the **left joystick** as two `Kind.Hold` levers (`XrCabControls` "crane"); remote: the left stick, not turned by the head | one two-axis grip per joystick |
| crane: hoist (#618, hold; `arm_boom_*`) | ↑ ↓ / right stick Y | new: cab: the **right joystick** (aft raises); remote: the right stick's Y as the axis (`XrPad`, while `CraneWorking`) | keep |
| crane: hook on / set down (#618, `couple`) | H / D-pad ← | new: cab: a **poke on the left console**; remote: right stick ← | keep |
| crane: hook camera (#618, `camera_toggle`) | V / R3 | gap: a headset cannot be handed another camera | a screen on the cab's console showing the trolley camera |
| arm_slew_left/right, arm_stick_out/in (#611, hold) | A D, W S / L stick X, Y in dig mode | ok: the **left joystick** by hand, as two levers (`Kind.Hold`: side to side slews, fore and aft runs the stick) | one two-axis grip per joystick |
| wheel loader lift / tilt (#612, hold; the excavator's arm_boom and arm_bucket actions in work mode) | ↑ ↓ ← → / R stick in work mode | ok: the **two levers right of the seat** (`XrCabControls` "loader", `Kind.Hold`: aft lifts, aft rolls back), work mode on the console | keep |
| blade_raise / blade_lower (#614, mini excavator, hold, driving or digging) | Shift / Ctrl, RB / LB (the gear paddles, which a crawler has no use for) | ok: the **blade lever** low and outboard of the left joystick (`XrCabControls` "miniexcavator", `Kind.Hold`: aft raises) | keep |
| arm_boom_up/down, arm_bucket_curl/dump (#611, hold) | ↑ ↓, ← → / R stick Y, X in dig mode | ok: the **right joystick** by hand, as two levers (aft raises the boom, side to side the bucket); the right stick itself stays R3's action pad | one two-axis grip per joystick |
| gear 1-6, R, N | 1-6 ` 0 / — | ok: H-pattern lever by hand (#438) | keep |
| car shift up / down, clutch (#290, sequential or manual car gearbox) | Shift Ctrl C / RB LB B (the shoulders, not trick and boost, in such a car) | ok: grips, B (they replay as the shoulders) | keep |
| car gates 1-6, R, N (#290, manual car gearbox) | 1-6 ` 0 / — | gap: no gear lever in the car cockpit's VR controls | **grip the car's gear lever** and move it through the gate, as the trucks' H-pattern lever (#438) |
| retarder | ' ; / — | ok: stalk by hand (#438) | keep |
| bus kneel / destination | K N / L3 — | ok: dash pokes (#438) | keep |
| farm: lower / raise the implement or header (#494) | K / L3 | ok: the kneel dash poke, or L3 | **grip the linkage lever** on the right console |
| farm: combine auger out / in (over a parked or a driven tipper), deliver at a co-op (#494) | N / X | ok: the destination dash poke, or X | **grip the auger joystick** on the console |
| farm: tip the tipping trailer's bin, delivering at a co-op (#494) | N / X (the same action, coupled to a tipper) | ok: the destination dash poke, or X | **grip the tipping valve lever** on the right console |
| farm: couple an implement or tipping trailer (#494) | H / D-pad ← | ok: R stick ← | **on foot, grip the lower link** to hitch it |
| farm: take a sack from a tank or trailer, on foot (#494) | E (hold / with Shift: 10) / Y (hold: 10) | ok: Y, hold for ten | **grip a sack** off the heap |
| farm: open a farm stand (stock, take back, collect / buy) (#494) | E / Y | ok: Y, or reach out and grip the stand (`TryInteract(byHand)`); the panel by laser + trigger | **grip the honesty box** to open it |
| farm: sell sacks on foot at a sugar factory or mill (#494) | E / Y | ok: Y, or reach out and grip (`TryInteract(byHand)`) | keep |
| board a landed UFO sighting's saucer (#863) | E / Y ("Get in the ufo" at the ramp's foot) | ok: Y, or reach out and grip (`TryInteract(byHand)`): it is a parked vehicle | keep |
| farm: set up / pack up a farm stand (#494) | LMB / RB with the stand in hand, empty hand to pack up | ok: R trigger (`use_item`) | keep |
| mini dumper: tip the skip (#614; the destination action, stopped) | N / — | ok: the **skip's button** on the console right of the wheel (`XrCabControls` "minidumper", `Kind.Poke`) | keep |
| tipper: tip the body / mixer: discharge (#613; the destination action, stopped) | N / — | ok: the destination dash poke (`XrCabControls` "-work") | grip a tipping valve lever |
| steamer whistle | H / D-pad ← | ok: R stick ←, or pull the cord (#438) | keep |
| look_behind | B / wheel button | ok: turn your head; held on a keyboard or wheel the view turns round (#436) | keep |

## Flying

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| pitch / roll | WASD / L stick | ok | + optional yoke / stick grab |
| throttle / climb / descend | Shift Space Ctrl / RT LT A B | ok: R / L trigger, analog (#436) | keep |
| fire / pigeon drop | LMB / RB | ok: R trigger | keep |
| airliner flaps | F6 F7 / LB RB | ok: grips, or the flap lever by hand (#438) | keep |
| speedbrake / gear / park brake | / G . / D-pad ← X, hold D-pad ← (#421) | ok: levers and poke by hand (#438), or hold R stick ← | keep |
| cockpit view (aircraft, #421) | V / R3 | ok: R3 shows or hides your own body | keep |
| autopilot / trim | Y Home End / hold D-pad → (AP, #421), trim — | ok: AP poke or hold R stick →, trim wheel by hand (#438) | gap: trim on a pad |
| wingsuit / canopy | stick, Space | ok: stick, A; arms roll the suit, hands pull the brakes (#438) | keep |
| pigeon flap / dive | Space Ctrl / A B | ok: A B, flap the arms (#438) | keep |
| UFO up / down, move, turn (#861) | Space Ctrl, WASD, mouse / RT LT, L stick, R stick | ok: R / L trigger, L stick; turns like the helicopter | keep |
| UFO boost (#861) | Shift or Q / L3 or LB | ok: L stick click, or the left grip | keep |
| UFO tractor beam (#861) | H / D-pad ← (`tractor_beam`) | ok: R stick ← | gap: a beam switch to grip on the dome's console (`XrCabControls` "ufo") |
| UFO ramp (landed) / legs (flying) (#861) | G / X (`car_door`) | ok: X | gap: a lever on the console, as the airliner's gear |
| UFO rim lights (#861) | L / D-pad → | ok: R stick → | keep |

## World, UI, others

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| menu | Esc / Start | ok: Menu tap | keep |
| accept an invite (#840) | tap F2 / Start › first entry | ok: Menu tap › first entry, wrist menu › Invites | keep |
| teleport (place search, BR map) | M / — | ok: wrist menu (#437), hand-held map with point and pull (#439) | keep |
| help / debug | F1 F9 / — | help: wrist menu (#437); debug: gap | debug in the wrist menu for admins |
| traffic endpoints (#971) | debug menu (F9): mouse / — | gap, as the rest of the debug menu | with the debug menu |
| playtest_panel (Debug `--playtest`, #751) | F10 / L3 + R3 | ok: both stick clicks (XrPad), the panel on the XrUi screen; typing to Claude: gap (no keyboard) | the laser key grid planned for the chat |
| playtest_note (Debug `--playtest-record`, #844) | F12 / panel › Note | ok: the panel's Note button (L3 + R3, XrPad) marks the moment with a screenshot, "✔ Note N saved" on the panel; End session shows the report's path there (#1383); its text: gap (no keyboard) | the laser key grid planned for the chat |
| playtest_topview / _scrub (#844) | Shift+F12, Ctrl+F12 / panel › Top view | ok: the panel's Top view button toggles it on the XrUi screen; zoom and scrub (Shift/Ctrl + wheel): keyboard only, a tuning aid | — |
| fly camera up / down / boost | Space Ctrl… / A B L3 | ok: A B L3 | keep |
| BR jump out / spectate | E, ←→ / Y, D-pad | ok | spectate on R stick ←/→ |
| Versteckis round tool: tag, sniff, decoy, sprint, moo, the Disguise radial (#1268) | LMB `use_item` / RB | new: trigger (the item seam); the radial by laser | tag by touching a prop with a hand + trigger |
| Versteckis: copy a decor prop (#1268) | RMB + LMB / LB + RB | new: point the hand at it, trigger | keep |
| Versteckis: sprint burst while disguised (#1268) | Shift `sprint` / L3 | new: left stick click | keep |
| Versteckis: settle the prop (#1268) | Ctrl / C `crouch_slide` / B | new: B, or crouch for real | keep |
| Versteckis: hold at the bell, the Höhenfeuer; take and set down the milk can (#1268) | hold E `interact_mount` / hold Y | new: hold Y through `XrPad`; a grip near the site (`TryInteract(byHand)`) starts it | grip the bell rope, the wood stack, the can (after a headset playtest) |
| Versteckis: follow-cam next / previous (#1268, `Ui/FollowCam`) | ← / → / D-pad ← → | new: the D-pad through `XrPad` | the right stick |
| Versteckis: open, join, leave (#1268) | Activities tab, `/vs` / quick menu › Activities | new: wrist menu › Activities | keep |
| Älplerfest flag swinging: join at the flag ground (#1269 part 5) | `interact_mount` E / Y within 16 m of the stage (`FahnenGame.TryInteract`) | new: Y through `XrPad`; a grip at the ground (`TryInteract(byHand)`) | grip the flag stand (after a headset playtest) |
| Älplerfest flag swinging: draw the figure (up, down, sweep, circle, eight) before its last beat (#1269) | mouse motion / right stick (`GestureRing`, 8 sectors at 30 Hz) | new: the right stick through `XrPad` | **swing the flag by hand** (the controller's path through `GestureRing`) (`gap`) |
| Älplerfest flag swinging: high throw and catch (#1269) | hold `use_item` LMB, release on a beat, press to catch | new: RB / the trigger through `XrPad` | throw it by hand (`gap`) |
| Désalpe: dress a cow, next headdress shape (#1269 part 7) | `interact_mount` E within 4.5 m of a cow of the herd on the alp (`DesalpeGame.TryInteract`) | new: Y through `XrPad`; a grip at the cow (`TryInteract(byHand)`) | hold the headdress to the cow's head and let go (`gap`) |
| Désalpe: next headdress colour; pick a herd in the fair's ring (#1269) | `use_item` LMB (the items let go of it while a cow or the ring is at hand) | new: X / the trigger through `XrPad` | keep |
| Désalpe: start the Alpabzug at the alp sign; give the vote in the ring (#1269) | `interact_mount` E | new: Y through `XrPad`; a grip at the sign or the ring | grip the sign, the notice board (after a headset playtest) |
| Christmas market: taste at a stall (the day's stamp), buy its ware, take the Samichlaus's bag (#1348) | `interact_mount` E within 5.5 m of a stall or 4.5 m of the Samichlaus (`ChristmasGame.TryInteract`) | new: Y through `XrPad`; a grip at the stall | grip the stall's counter or the mug on it (`gap`: the pad route works) |
| Christmas market: drink the Glühwein mug (#1348) | `use_item` LMB (`ItemController`, like any drink) | X / the trigger through `XrPad` | hold the mug to the mouth (the drink pose, as the beer) |
| Escalade: smash the Marmite (youngest and oldest of the party both press), ladle the soupe for the party, start the Course de l'Escalade (#1351) | `interact_mount` E within 5 m of the Marmite, 4.5 m of the pot or 5 m of the start line (`EscaladeGame.TryInteract`) | new: Y through `XrPad`; a grip at the Marmite, the pot or the start line | grip the Marmite's lid and bring a hand down, grip the ladle (`gap`: the pad route works) |
| Chalandamarz: begin the procession at the Treichel stand, then ring the Treichel on the march beat while walking the loop (#1356) | `interact_mount` E within 5 m of the stand to begin, then every E rings (`ChalandaGame.TryInteract`) | new: Y through `XrPad`; a grip at the stand to begin, then a grip on each beat (the walk is the stick or room scale) | swing the hand like a cowbell, the shake read from the controller's motion (`gap`: the pad route works) |
| Escalade: eat the chocolate or the soupe (#1351) | `use_item` LMB (`ItemController`, like any food) | X / the trigger through `XrPad` | bring the bowl or the piece to the mouth (the eat pose) |
| Fasnacht: take a Larve and Räppli at the stall, march at Basel's lantern, sit down at the Guggenmusik stage (#1357) | `interact_mount` E within 5 m of the stall, 6 m of the lantern, 7 m of the stage (`FasnachtGame.TryInteract`) | new: Y through `XrPad`; a grip at the stall, the lantern or the stage | grip a mask on its peg, the lantern's handle, a drumstick (`gap`: the pad route works) |
| Fasnacht: tap the Guggenmusik's notes; throw Räppli (#1357) | `use_item` LMB (or `jump`) while a song runs (`FasnachtGame`); `use_item` with the bag in hand throws a handful (`ItemUse.Confetti`) | the trigger (RT) through `XrPad` | strike a drum with the controller, toss a handful by hand (`gap`: the pad route works) |
| Sechseläuten: step the minute of a bet at the Böögg's pyre (#1358) | `use_item` LMB while at the pyre with bets open (`SechseGame.TakesUse`, like the Désalpe vote) | X / the trigger through `XrPad` | turn a dial by hand (`gap`: the pad route works) |
| Sechseläuten: place the bet, grill a Cervelat on the embers, take the Zunft's purse (#1358) | `interact_mount` E within 14 m of the pyre or 4 m of the Weibel (`SechseGame.TryInteract`) | new: Y through `XrPad`; a grip at the pyre or the Weibel | drop a coin in the stake box, hold a sausage in the fire (`gap`: the pad route works) |
| Sechseläuten: eat the grilled Cervelat (#1358) | `use_item` LMB (`ItemController`, like any food) | X / the trigger through `XrPad` | bring it to the mouth (the eat pose) |
| 1 August: take a lampion, buy a box of fireworks, start the lampion walk (#1355) | `interact_mount` E within 4.5 m of a stand or 5 m of the post (`August1Game.TryInteract`) | new: Y through `XrPad`; a grip at the stand or the post | grip the stall's counter, the lantern or the pole (`gap`: the pad route works) |
| 1 August: hold the lampion (a light), light a box of fireworks (#1355) | the lampion is held like the torch (the hotbar); `use_item` LMB lights a box (`ItemUse.Firework`, `August1Game.TryLight`) | the lampion in the hand as any item; X / the trigger through `XrPad` lights the box | hold the lantern up in the hand (the hand is its light); strike the box on the ground (`gap`) |
| Désalpe: drive the herd (#1269) | walk behind the herd; a body in a cow's flight zone (5 m, 9 m mounted) moves it away | new: the same, the body is the head and the controllers are not used | walk or ride behind it for real; waving it on by hand (`gap`) |
| loot, shop, lock, Simon, vending | mouse + keys | ok: laser + trigger | lock dial by **wrist twist** |
| chat | Enter, / | partly (#1277): the quick menu's Chat wedge opens it with the "/" picker, the commands and their words as buttons the ray presses; free text still no keyboard | laser key grid on the `XrUi` panel, for free text |
| chat coordinates: Teleport, Track, Show on map, Copy (#1277) | click a point's pin or coordinates; pad: focus its line, A | ok: the ray on the open chat (`XrUi` draws it) | keep |
| watch | — | ok: Swiss watch on the drawn left wrist (#439, #1097); read it and the radial menu opens over it | keep |
| save_clip: the replay buffer into the movie studio (#638) | F5 / Start > Save clip | ok: Menu tap > Save clip | a wrist-menu entry |
| movie studio timeline (#638, markers #656, camera keys #669, cameras #675) | Space J K L, ← →, S, Del, M, I, V, 1-9, C / Y, LT RT, D-pad, X (hold: cut to camera), R3, View, L3, R stick, LB RB | ok: the same pad buttons through `XrPad`, the panel by laser | grip the playhead and scrub by hand; timeline on the `XrUi` panel (#637 milestone 3) |
| free_cursor (#654) | tap Alt / — | n/a: VR points with the laser, a pad has no cursor | keep |
| map screen: pan / zoom / draw / tool / search (#515, #799) | arrows, drag, wheel, T, F / L stick, LB RB, A, Y, X | ok on the panel (#799): the ray is the pointer, trigger click puts the marker down, trigger drag pans, left stick pans, on-map + / − zoom (the shoulders are muted there: in VR they are the trigger), popular places instead of typing; draw tools pointable under Advanced | a table-top relief map you reach into (#535)

## Coherence findings (what violated the rules)

Status after #436: 1, 2, 5, 6 fixed; 3 and 9 kept on purpose (below); 4 and 7 go to the hands
work (#437, #438); 8 fixed by #435.

1. Flight, GPX replay and the garage read `Input.GetJoyAxis(0, Trigger*)` raw (`FootPlayer.FlyPhysics`,
   `Gpx/PlaybackCamera`, `Vehicles/GarageUi`): they skip the input map and VR's parsed pad (R2).
2. D-pad ← and ↓ cannot be reached in VR, and R stick ↓ sends D-pad →, which is "next item" on foot
   but "lights" mounted (R3).
3. R stick ↑ is the emote wheel on foot and the engine mounted. Kept: one meaning per context is
   what R2 asks, and the two contexts never overlap.
4. A free grip is take_wheel / trick / boost; a grip should mean "grab what is near" (R1).
5. The VR monitor's F7 swallows the keyboard's flaps_down (`XrMonitor`).
6. `look_behind` is bound, listed in F1 and in the wheel presets, but never read.
7. About 20 actions are keyboard only, with no pad or VR way (R5).
8. In VR, prompts show Xbox names (`PlayerInput` pins `Gamepad`), and about 20 UI strings type keys
   by hand (R6).
9. `VendingUi` and `RadioUi` read logical keycodes; `LockPickUi`, `ShopUi` and crafting read raw
   Shift (physical-key rule, `core/key-hints`). Kept: the vending code and the radio's `/` and
   Ctrl+F are typed characters, so the printed letter is the right one; Shift sits in the same
   place on every layout, and the pad paths (sprint, the catalogue's X / Y) already exist.
| Älplerfest alphorn row (#1269): take a seat in the song | E `interact_mount` / Y | new: Y through `XrPad`; a grip near the row (`TryInteract`) is the same call | grip a horn of the row |
| Alphorn: blow (hold from a note's start to its end) | LMB `use_item` / RB or RT | new: the trigger (replays as the pad's RT through `XrPad`) | raise the horn to the mouth and blow (a later target) |
| Alphorn: choose the note (partial) | wheel, W / S / R stick up-down | new: the right stick up-down through `XrPad` | keep |
| Yodel: tap on the beat | Space `jump` / A | new: A | keep |
| Zibelemärit: taste at a stall (the day's stamp), buy its ware (#1373) | `interact_mount` E within 5.5 m of a stall (`ZibeleGame.TryInteract`) | new: Y through `XrPad`; a grip at the stall | grip a braid off the awning or the kettle's ladle (`gap`: the pad route works) |
| Zibelemärit: throw a confetti egg or Räppli on the square (#1373) | `use_item` LMB with the egg in hand (`ItemUse.Confetti`) | X / the trigger through `XrPad` | toss it by hand (`gap`: the pad route works) |
| Montreux Jazz: mark "I am listening" at the stage-front rail (#1394) | `interact_mount` E within 3.5 m of the rail (`JazzGame.TryInteract`) | new: Y through `XrPad`; a grip at the rail | grip the rail in front of the stage (`gap`: the pad route works) |
| Montreux Jazz: clap the band back for the bis on the ring's beats (#1394) | `jump` Space while the call is on in the audience (`JazzGame`, `FootPlayer.JumpTaken`, as the pogo's) | A | the pad's A through `XrPad` (clapping by hand is not built: `gap`) |
| Zibelemärit: eat the tart or the soup (#1373) | `use_item` LMB (`ItemController`, like any food) | X / the trigger through `XrPad` | bring it to the mouth (the eat pose) |
