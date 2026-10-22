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
| radio on / off (#725) | tap E (pointed) or tap Use (held) / tap Y or the Use button; hold either for the panel | ok: poke the radio's red key with a controller tip (`XrHands.PokeRadio`); grip still opens the panel | keep |
| elevator call / floor list (#557) | E / Y at the call button or in the cabin | ok: Y, or grip the call button or the cabin's panel; the list is a pointable panel (`XrUi`) | keep |
| flat door, its lock (#557) | E / Y at the door | ok: Y, or grip the door; the dial on the stick | keep |
| ride_menu | R / Y with nothing near | ok: Y, wrist menu (#437) | keep |
| car_door / gather | G / X (tap / hold) | ok: X; doors by grip | gather = **grip and pull** the resource |
| emote_wheel | B / D-pad ↑ | ok: hold R stick ↑, aim with the right hand (#437) | keep |
| phone (#889): out, quick action, open, back | ↑, ← → Enter, ↑, Esc ↓ / quick menu › Phone, D-pad A, D-pad ↑, B | ok (#920): grip at the left front pocket or wrist menu › Phone; the real index tip taps, drags and swipes the glass, or the right ray and trigger press from afar (`XrPhone`); the free hand swings it open, X opens / folds, B back / away; the arm aims the camera app, the holding trigger shoots, the right trigger (ray off the phone) steps the zoom, turned round it is a selfie; raised to the eyes it travels (a blink after 3 s), to an ear it answers; let go it goes home to the pocket | keep |
| challenge / accept a fist fight (#495) | E / Y, looking at the player | ok: Y, or reach out and grip | keep |
| fight step / jump / crouch (#495) | A D, W Space, S Ctrl / L stick, A | ok: L stick towards / away, A, crouch for real (`XrPad.RealCrouch`) | room-scale stepping |
| fight_punch / fight_kick (#495) | LMB J, RMB K / X RB, Y LB | ok: R / L trigger | **punch and kick for real** (hand speed, R4) |
| fight_block (#495, hold) | Shift L / B | ok: B (`XrPad.RightB`, not the crouch's B) | forearms raised in front of the face |
| aura_next (#907, timed on the downbeat; #914 the waiter's straight face) | Space, LMB / A, RB | ok: R trigger (sends RB on foot), or **strike the next move's pose for real** (`AuraBody.Classify` on `VrHands`, #914) | keep |
| aura planner: add / aura_remove / aura_lock / aura_suggest (#907) | click, arrows Enter / Backspace / F / G; A / X / Y / R3 | ok: stick + A / X / Y / R stick click | point at the cards with the laser |
| toggle_mode | T / D-pad ↓ | ok: wrist menu (#437) | keep |
| creative mode (#1002): toggle_mode, then jump twice to fly, jump / crouch up and down, boost | T, Space Space, Space / Shift, Ctrl / D-pad ↓, A A, A / B, L3 | ok: wrist menu, then the pad's A A, A / B, L3 | keep |
| spectator camera and back (#1002) | Shift+T / hold D-pad ↓ | ok: wrist menu "Spectator camera" | keep |
| climb ladder | W / S on a ladder | ok: stick; rock faces and walls hand over hand (#439, stamina) | keep |

## Items, building, inventory

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| use_item / aim_item | LMB, RMB / RB, LB | ok: R / L trigger | camera and binoculars **raised to the eye** = aim |
| drop_item | Q / — | ok: fling an empty squeeze, or the wrist menu (#437) | a real throw by release velocity |
| next / prev_item, slot_1-6 | wheel, 1-6 / D-pad → | ok: R stick ↓, grip at the right / left hip (#437) | slots shown at the hip |
| inventory | I, Tab / Back (tap; opens on the release) | ok: Menu hold | keep |
| quick_menu (#840) | hold F2 / hold Back | ok: wrist menu Activities + Invites entries; a ring opened by Back is aimed by the right hand | a hold of Menu longer than the inventory's pulse |
| invite_decline (#840) | Shift+F2 / — | ok: wrist menu › Invites › Decline (laser), or Menu tap › Activities | keep |
| quick_wheel | X / D-pad ← | ok: hold a grip at the left hip, aim with the right hand, let go to pick (#489) | keep |
| build_turn, next piece | R, aim+wheel / D-pad ↑, LB+D-pad → | gap | **twist the hand** while aiming; R stick ←/→ steps the piece |
| fishing rod: cast, strike, reel, wind in (#493) | hold LMB + let go, LMB, hold LMB, RMB / the same on RB, LB | ok: R trigger (hold, let go: cast; press: strike; hold: reel), L trigger winds in | gap: **flick the rod hand** to cast (release speed = distance), **crank the reel** with the left hand (R1, R4) |
| bird_journal (birds and fish pages, #493) | J / — | ok: wrist menu (#437); the page buttons by pointer | keep |
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
| playtest_note (Debug `--playtest-record`, #844) | F12 / panel › Note | ok: the panel's Note button (L3 + R3, XrPad) marks the moment with a screenshot; its text: gap (no keyboard) | the laser key grid planned for the chat |
| playtest_topview / _scrub (#844) | Shift+F12, Ctrl+F12 / panel › Top view | ok: the panel's Top view button toggles it on the XrUi screen; zoom and scrub (Shift/Ctrl + wheel): keyboard only, a tuning aid | — |
| fly camera up / down / boost | Space Ctrl… / A B L3 | ok: A B L3 | keep |
| BR jump out / spectate | E, ←→ / Y, D-pad | ok | spectate on R stick ←/→ |
| loot, shop, lock, Simon, vending | mouse + keys | ok: laser + trigger | lock dial by **wrist twist** |
| chat | Enter, / | gap: no keyboard | laser key grid on the `XrUi` panel |
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
