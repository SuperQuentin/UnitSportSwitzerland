# Input conventions: what other games do, and the rules we follow

Research digest from October 2026: Minecraft, ARC Raiders, Valorant, Fortnite, Rust, Tarkov and Apex.
It is the reference for choosing a key, an inventory gesture or an interaction rule. Defaults change
with patches, so check a game before quoting it as a hard fact.

## Default PC bindings, compared

| Action | Minecraft | ARC Raiders | Valorant | Fortnite | Rust | Tarkov |
|---|---|---|---|---|---|---|
| Interact / use | RMB | E | F | E | E | F |
| Reload | n/a | R | R | R | R | R |
| Inventory | E | Tab | n/a | I | Tab | Tab |
| Drop | Q (Ctrl+Q stack) | in the panel | G | in the panel | drag out of the panel | in the panel |
| Slots | 1-9, wheel | 1-3, wheel | 1-4 | 1-6 | 1-6 | 1-0 |
| Map | n/a | M | M | M | G/M | O |
| Ping | n/a | MMB | Z | MMB | MMB | n/a |
| Abilities / lean | n/a | Q items | C Q E X | building | Q craft | Q/E lean |
| Crouch / sprint | Shift sneak / Ctrl | Ctrl or C / Shift | Ctrl / Shift walks | Ctrl / Shift | Ctrl / Shift | C / Shift |

## How the keys are chosen

- **Home ring.** The left hand rests on WASD. Space, Shift and Ctrl are reached by thumb and pinky.
  Q E R F C X Z G V and 1-5 are one finger move away. Frequent actions stay inside that ring;
  rare panels go further out (M map, Tab or I inventory, T or Enter chat, F-keys).
- **One context-sensitive interact key**, usually E (F when Q and E are abilities or lean). It does
  whatever the *targeted* object offers.
- **Fixed meanings.**
  - R: reload, or the secondary use of the held thing.
  - Ctrl / C: crouch.
  - Shift: sprint.
  - MMB: ping.
  - Wheel: cycles slots.
  - Numbers: slots, in screen order.
  - Inside a menu, Shift / Ctrl / Alt + click are quick-transfer modifiers.
- **Hold or tap.**
  - Hold for lasting states (sprint, aim, push-to-talk) and for radial wheels (hold to show,
    release to pick).
  - Tap for one-shot actions.
  - Offer a toggle for every hold (accessibility).
- **Panels.** The key that opens a panel closes it, and Esc always closes it. Menu-context bindings
  are separate from gameplay ones, so one key may mean two things without a clash.
- **Rebinding** is expected on PC, with conflict detection in the binding screen.

## Inventory vocabulary (Minecraft is the reference)

| Gesture | Effect |
|---|---|
| LMB | Take the stack, place the stack, or swap with the slot |
| RMB | Take half; while carrying, put one |
| Drag with LMB / RMB while carrying | Spread the stack evenly / put one per slot |
| Shift + click | Quick-move to the other area (hotbar, pack, container). Rust: Shift+RMB the stack, Ctrl+RMB one item |
| Double-click | Collect matching items into one stack |
| 1-N over a slot | Swap with that hotbar slot |
| Q / Ctrl+Q over a slot | Drop one / drop the stack |
| Release outside the panel | Drop into the world (Minecraft, Rust) |

**Drag-and-drop rules:**
- A few pixels of movement before a press becomes a drag.
- A ghost of the item with its count follows the cursor.
- The target slot is highlighted for merge or swap.
- Esc or RMB cancels and returns the item to where it came from.
- Every drag has a click equivalent (modifier-click or a context menu).

## World interaction

- **Targeting:**
  1. Cast a ray from the camera centre (2-3 m for human-scale things).
  2. If nothing is hit, look in a small cone around it.
  3. Score the candidates by angle, then distance, then a priority.
  4. Check line of sight. Add a little hysteresis so the target does not flicker.
- **Never take the key for something the player is not looking at.** Consume it only when there is
  a target; otherwise the key keeps its other meaning. One owner per press.
- **Prompt:** one prompt, in the form `[E] Verb Object`, using the live binding (`InputHints`), with
  an outline on the target.
- **Hold to interact** (with a progress ring) is only for slow or consequential actions.

## Radio and music players

- **GTA:** hold for a station wheel (time slows); tap for the next station.
- **Cyberpunk:** tap R for the next station, hold for the list.
- **Fallout:** a Pip-Boy tab lists stations.
- **Rust boombox:** LMB plays a cassette, RMB opens the station picker.
- **Minecraft jukebox:** the block itself is the interface.
- **What works:**
  - A simple player first, with the full picker one step deeper.
  - Off is always reachable.
  - Instant audio switch plus a "Now playing" note.
  - Volume separate from the station.
  - State kept when you leave.

## Rules for this project

- E is the only world-interact key. It acts on the pointed thing, never on something merely within
  a radius (`items/radio`, `vehicles/door-reach`).
- Every gameplay key is an `InputMap` action (`core/input`). Never read a physical key for a
  gameplay control, so a future rebinding screen covers it.
- Inventory panel gestures follow the table above (`items/cursor-inventory`).
- Panels close on the key that opened them and on Esc (`core/menu-refuses-close-still-consume`).

**Sources:**
- Minecraft Wiki: Inventory, Jukebox.
- arcraiders.wiki: Settings.
- Shacknews and Prima Games: ARC Raiders controls.
- Facepunch Wiki: Rust keybinds.
- GameSpot: Tarkov controls.
- ProSettings: Valorant keybinds.
- Meta: raycasting best practices.
- Smart Interface Design Patterns: drag-and-drop UX.
- Game Accessibility Guidelines.
