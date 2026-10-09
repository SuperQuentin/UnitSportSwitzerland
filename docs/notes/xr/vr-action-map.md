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
| radio on / off (#725) | tap E (pointed) or tap Use (held) / tap Y or the Use button; hold either for the panel | ok: poke the radio's red key with a controller tip (`XrHands.PokeRadio`); grip still opens the panel | keep |
| elevator call / floor list (#557) | E / Y at the call button or in the cabin | ok: Y, or grip the call button or the cabin's panel; the list is a pointable panel (`XrUi`) | keep |
| flat door, its lock (#557) | E / Y at the door | ok: Y, or grip the door; the dial on the stick | keep |
| ride_menu | R / Y with nothing near | ok: Y, wrist menu (#437) | keep |
| car_door / gather | G / X (tap / hold) | ok: X; doors by grip | gather = **grip and pull** the resource |
| emote_wheel | B / D-pad ↑ | ok: hold R stick ↑, aim with the right hand (#437) | keep |
| challenge / accept a fist fight (#495) | E / Y, looking at the player | ok: Y, or reach out and grip | keep |
| fight step / jump / crouch (#495) | A D, W Space, S Ctrl / L stick, A | ok: L stick towards / away, A, crouch for real (`XrPad.RealCrouch`) | room-scale stepping |
| fight_punch / fight_kick (#495) | LMB J, RMB K / X RB, Y LB | ok: R / L trigger | **punch and kick for real** (hand speed, R4) |
| fight_block (#495, hold) | Shift L / B | ok: B (`XrPad.RightB`, not the crouch's B) | forearms raised in front of the face |
| toggle_mode | T / D-pad ↓ | ok: wrist menu (#437) | keep |
| climb ladder | W / S on a ladder | ok: stick; rock faces and walls hand over hand (#439, stamina) | keep |

## Items, building, inventory

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| use_item / aim_item | LMB, RMB / RB, LB | ok: R / L trigger | camera and binoculars **raised to the eye** = aim |
| drop_item | Q / — | ok: fling an empty squeeze, or the wrist menu (#437) | a real throw by release velocity |
| next / prev_item, slot_1-6 | wheel, 1-6 / D-pad → | ok: R stick ↓, grip at the right / left hip (#437) | slots shown at the hip |
| inventory | I, Tab / Back | ok: Menu hold | keep |
| quick_wheel | X / D-pad ← | ok: hold a grip at the left hip, aim with the right hand, let go to pick (#489) | keep |
| build_turn, next piece | R, aim+wheel / D-pad ↑, LB+D-pad → | gap | **twist the hand** while aiming; R stick ←/→ steps the piece |
| fishing rod: cast, strike, reel, wind in (#493) | hold LMB + let go, LMB, hold LMB, RMB / the same on RB, LB | ok: R trigger (hold, let go: cast; press: strike; hold: reel), L trigger winds in | gap: **flick the rod hand** to cast (release speed = distance), **crank the reel** with the left hand (R1, R4) |
| bird_journal (birds and fish pages, #493) | J / — | ok: wrist menu (#437); the page buttons by pointer | keep |
| farm by hand (#494) | LMB / RB with a hoe, seed or fertiliser in hand | ok: R trigger (`use_item`, no new action) | a hoe **swung down** at the ground |
| harvest a ripe field cell (#494) | hold G / X | ok: hold X (`gather`, the gathering hold) | **grip and pull** the crop, like gather |

## Mounted and driving

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| throttle / brake / steer | W S A D / RT LT stick | ok: triggers; **wheel by grip** | keep |
| tuck_boost / trick / boost | Shift F Q / X RB LB | ok: X, grips | keep |
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
| arm_slew_left/right, arm_stick_out/in (#611, hold) | A D, W S / L stick X, Y in dig mode | ok: the **left joystick** by hand, as two levers (`Kind.Hold`: side to side slews, fore and aft runs the stick) | one two-axis grip per joystick |
| wheel loader lift / tilt (#612, hold; the excavator's arm_boom and arm_bucket actions in work mode) | ↑ ↓ ← → / R stick in work mode | ok: the **two levers right of the seat** (`XrCabControls` "loader", `Kind.Hold`: aft lifts, aft rolls back), work mode on the console | keep |
| blade_raise / blade_lower (#614, mini excavator, hold, driving or digging) | Shift / Ctrl, RB / LB (the gear paddles, which a crawler has no use for) | ok: the **blade lever** low and outboard of the left joystick (`XrCabControls` "miniexcavator", `Kind.Hold`: aft raises) | keep |
| arm_boom_up/down, arm_bucket_curl/dump (#611, hold) | ↑ ↓, ← → / R stick Y, X in dig mode | ok: the **right joystick** by hand, as two levers (aft raises the boom, side to side the bucket); the right stick itself stays R3's action pad | one two-axis grip per joystick |
| gear 1-6, R, N | 1-6 ` 0 / — | ok: H-pattern lever by hand (#438) | keep |
| retarder | ' ; / — | ok: stalk by hand (#438) | keep |
| bus kneel / destination | K N / L3 — | ok: dash pokes (#438) | keep |
| farm: lower / raise the implement or header (#494) | K / L3 | ok: the kneel dash poke, or L3 | **grip the linkage lever** on the right console |
| farm: combine auger out / in (over a parked or a driven tipper), deliver at a co-op (#494) | N / X | ok: the destination dash poke, or X | **grip the auger joystick** on the console |
| farm: tip the tipping trailer's bin, delivering at a co-op (#494) | N / X (the same action, coupled to a tipper) | ok: the destination dash poke, or X | **grip the tipping valve lever** on the right console |
| farm: couple an implement or tipping trailer (#494) | H / D-pad ← | ok: R stick ← | **on foot, grip the lower link** to hitch it |
| farm: take a sack from a tank or trailer, on foot (#494) | E (hold / with Shift: 10) / Y (hold: 10) | ok: Y, hold for ten | **grip a sack** off the heap |
| farm: open a farm stand (stock, take back, collect / buy) (#494) | E / Y | ok: Y, or reach out and grip the stand (`TryInteract(byHand)`); the panel by laser + trigger | **grip the honesty box** to open it |
| farm: sell sacks on foot at a sugar factory or mill (#494) | E / Y | ok: Y, or reach out and grip (`TryInteract(byHand)`) | keep |
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

## World, UI, others

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| menu | Esc / Start | ok: Menu tap | keep |
| teleport (place search, BR map) | M / — | ok: wrist menu (#437), hand-held map with point and pull (#439) | keep |
| help / debug | F1 F9 / — | help: wrist menu (#437); debug: gap | debug in the wrist menu for admins |
| playtest_panel (Debug `--playtest`, #751) | F10 / L3 + R3 | ok: both stick clicks (XrPad), the panel on the XrUi screen; typing to Claude: gap (no keyboard) | the laser key grid planned for the chat |
| fly camera up / down / boost | Space Ctrl… / A B L3 | ok: A B L3 | keep |
| BR jump out / spectate | E, ←→ / Y, D-pad | ok | spectate on R stick ←/→ |
| loot, shop, lock, Simon, vending | mouse + keys | ok: laser + trigger | lock dial by **wrist twist** |
| chat | Enter, / | gap: no keyboard | laser key grid on the `XrUi` panel |
| watch | — | ok: Swiss watch on the left wrist (#439) | keep |
| save_clip: the replay buffer into the movie studio (#638) | F5 / Start > Save clip | ok: Menu tap > Save clip | a wrist-menu entry |
| movie studio timeline (#638) | Space J K L, ← →, S, Del / Y, LT RT, D-pad, X, R stick, LB RB | ok: the same pad buttons through `XrPad`, the panel by laser | grip the playhead and scrub by hand; timeline on the `XrUi` panel (#637 milestone 3) |
| free_cursor (#654) | tap Alt / — | n/a: VR points with the laser, a pad has no cursor | keep |
| map screen: pan / zoom / draw / tool / search (#515) | arrows, drag, wheel, T, F / L stick, LB RB, A, Y, X | **gap, deferred on purpose**: the screen renders on the `XrPad` panel and says so, but the map itself is not pointable — a laser on a 2D map of a 3D country is the wrong answer to design in a hurry | a table-top relief map of Switzerland you reach into, grab to pan, pinch to zoom and paint tiles on with a finger (#535) |

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
