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
| move_* / sprint | WASD, L stick / Shift, L3 | ok: L stick head-relative, L3 | + arc **teleport** and room-scale (comfort setting) |
| look / turn | mouse, R stick | ok: head + snap 30° | snap angle 15/30/45 or smooth (setting) |
| jump | Space / A | ok: A | keep |
| crouch_slide | Ctrl, C / B | ok: B | **physical crouch** + B |
| interact_mount | E / Y | ok: Y; doors by grip | **grip the thing** (seat, ladder, crate, loot, radio, item); Y ranged fallback |
| ride_menu | R / Y with nothing near | ok: Y | + **wrist menu** |
| car_door / gather | G / X (tap / hold) | ok: X; doors by grip | gather = **grip and pull** the resource |
| emote_wheel | B / D-pad ↑ | gap: R stick ↑ (engine when mounted) | hold **L3** → radial aimed with the hand |
| toggle_mode | T / D-pad ↓ | gap | wrist menu |
| climb ladder | W / S on a ladder | ok: stick | **hand over hand** grab; rocks too (stamina) |

## Items, building, inventory

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| use_item / aim_item | LMB, RMB / RB, LB | ok: R / L trigger | camera and binoculars **raised to the eye** = aim |
| drop_item | Q / — | gap | **open the grip** holding it; throw by release velocity |
| next / prev_item, slot_1-6 | wheel, 1-6 / D-pad → | partial: R stick ↓ | **hip hotbar** (grip a slot) + R stick ↓ |
| inventory | I, Tab / Back | ok: Menu hold | keep |
| quick_wheel | X / D-pad ← | gap | hold **R3** → radial |
| build_turn, next piece | R, aim+wheel / D-pad ↑, LB+D-pad → | gap | **twist the hand** while aiming; R stick ←/→ steps the piece |
| bird_journal | J / — | gap | wrist menu |

## Mounted and driving

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| throttle / brake / steer | W S A D / RT LT stick | ok: triggers; **wheel by grip** | keep |
| tuck_boost / trick / boost | Shift F Q / X RB LB | ok: X, grips | keep |
| engine_toggle | Z / D-pad ↑ | ok: R stick ↑ | keep (R3) |
| lights_toggle | L / D-pad → | gap (R stick ↓ gives D-pad →, labelled "next item") | R stick → |
| roof / horn / couple | O H / D-pad ← | gap | R stick ← |
| tune | T / D-pad ↓ | gap | R stick ↓ |
| radio next / prev / panel | U P R / — | gap | **reach to the dash radio**: grip = panel, poke ← → |
| take_wheel (passenger) | F / RB | ambiguous (free grip) | **grip the wheel** from the passenger seat |
| shift up / down, clutch | Shift Ctrl C / RB LB B | ok: grips, B | keep |
| gear 1-6, R, N | 1-6 ` 0 / — | gap | **H-pattern lever by hand** (`XrLever`) |
| retarder | ' ; / — | gap | **stalk by hand** |
| bus kneel / destination | K N / — | gap | **dash buttons by poke** |
| steamer whistle | H / D-pad ← | gap | **pull the cord** |
| look_behind | B / — | dead binding (never read) | head turn; remove the action |

## Flying

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| pitch / roll | WASD / L stick | ok | + optional yoke / stick grab |
| throttle / climb / descend | Shift Space Ctrl / RT LT A B | gap: triggers dead (raw device-0 read) | **R / L trigger, analog** |
| fire / pigeon drop | LMB / RB | ok: R trigger | keep |
| airliner flaps | F6 F7 / LB RB | ok: grips | + **flap lever by hand** |
| speedbrake / gear / park brake | / G . / D-pad ← X — | partial: X only | **cockpit levers by hand** |
| autopilot / trim | Y Home End / — | gap | **poke** AP, trim wheel by hand |
| wingsuit / canopy | stick, Space | ok: stick, A | **arms spread** glide, lean steer; **pull the brake toggles** |
| pigeon flap / dive | Space Ctrl / A B | ok: A B | + **flap the arms** |

## World, UI, others

| Action | Kb / Pad | VR now | VR target |
|---|---|---|---|
| menu | Esc / Start | ok: Menu tap | keep |
| teleport (place search, BR map) | M / — | gap | **hand-held 3D Swiss map**; wrist menu |
| help / debug | F1 F9 / — | gap | wrist menu |
| fly camera up / down / boost | Space Ctrl… / A B L3 | ok: A B L3 | keep |
| BR jump out / spectate | E, ←→ / Y, D-pad | ok | spectate on R stick ←/→ |
| loot, shop, lock, Simon, vending | mouse + keys | ok: laser + trigger | lock dial by **wrist twist** |
| chat | Enter, / | gap: no keyboard | laser key grid on the `XrUi` panel |
| watch | — | — | **Swiss watch** on the left wrist (time, altitude, speed); a look opens the wrist menu |

## Coherence findings (what violates the rules today)

1. Flight, GPX replay and the garage read `Input.GetJoyAxis(0, Trigger*)` raw (`FootPlayer.FlyPhysics`,
   `Gpx/PlaybackCamera`, `Vehicles/GarageUi`): they skip the input map and VR's parsed pad (R2).
2. D-pad ← and ↓ cannot be reached in VR, and R stick ↓ sends D-pad →, which is "next item" on foot
   but "lights" mounted (R3).
3. R stick ↑ is the emote wheel on foot and the engine mounted (R2).
4. A free grip is take_wheel / trick / boost; a grip should mean "grab what is near" (R1).
5. The VR monitor's F7 swallows the keyboard's flaps_down (`XrMonitor`).
6. `look_behind` is bound, listed in F1 and in the wheel presets, but never read.
7. About 20 actions are keyboard only, with no pad or VR way (R5).
8. In VR, prompts show Xbox names (`PlayerInput` pins `Gamepad`), and about 20 UI strings type keys
   by hand (R6).
9. `VendingUi` and `RadioUi` read logical keycodes; `LockPickUi`, `ShopUi` and crafting read raw
   Shift (physical-key rule, `core/key-hints`).
