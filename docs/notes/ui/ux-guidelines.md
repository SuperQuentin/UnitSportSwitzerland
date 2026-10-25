# UI/UX guidelines: where things go, how big, how they behave

Read before creating, changing or reviewing **any** interface: menus, HUDs, pickers, popups,
inventory, settings, the phone, VR panels. `style-guide` says how a screen *looks* (glass, amber,
`UiTheme`/`UiKit`); this note says where things go, how big they are and how they behave, so the
player is comfortable rather than frustrated. Before finishing a UI task, run the **self-review
checklist** at the bottom and fix any failure, or say in the PR why it stays.

## 0. This project

- **Platforms:** PC (Windows, macOS, Linux), Android, iOS, VR (OpenXR, Quest over Link or native).
- **Inputs:** mouse + keyboard, gamepad, touch (`core/touch-controls`), VR controllers (laser pointer
  and direct touch, pad buttons through `XR/XrPad`). Every UI works with **all** of them
  (`general/new-action-three-devices`, `xr/vr-action-map`).
- **Resolutions:** 1920x1080 reference on PC; phones ~16:9 to ~21:9 with notches and rounded corners;
  tablets ~4:3; `--mobile` previews the phone layout on the desktop. In VR, size in meters and visual
  angle, not pixels.
- **Tokens:** `Ui/UiTheme` is the one shared file: colours (`Text`, `TextDim`, `TextFaint`, `Amber`,
  `Good`/`Warn`/`Bad`), fonts (`FontTiny` 11, `FontSmall` 13, `FontBody` 15, `FontHeading` 22,
  `FontTitle` 44), panels (`GlassPanel`, `Flat`), prompts (`Prompt`). Menu pages wear a skin
  (`menu-skins`): build with `UiKit` so both skins work. The generic scales below are for when a
  token is missing: add it to `UiTheme`, never type a literal locally.
- **Pace:** mixed. Open-world travel and menus are relaxed (more info allowed); driving, flying,
  races and Battle Royale are under pressure (minimal, glanceable HUD, big targets).
- **Conventions already set here:** Esc / pad B (`ui_cancel`) is Back everywhere and `Ui/Modal`
  answers it first; confirmations go through `Modal.Confirm`; prompts name controls through
  `InputHints` (`core/key-hints`), never a typed key; amber marks only selected/focused/primary.

## 1. Think like the player first

Before placing anything, answer (in your reasoning):

1. **What is the player trying to do on this screen?** 1-3 main goals; the UI serves those.
2. **How often is each element used?** Frequent = big, close, always visible. Rare = smaller,
   grouped, one level deeper.
3. **Where will they look for it first?** Use the place players know from other games (Jakob's
   law); don't invent a new location when a convention exists.
4. **What is their hand/cursor/focus doing right before and after?** Put the next action near the
   previous one.
5. **What state are they in?** Under pressure → minimal, glanceable, big targets. Relaxed → more info.
6. **What if they hit the wrong thing?** Destructive = far from common buttons, confirmed or undoable.

## 2. Placement

### Conventions

| Element | Expected place |
|---|---|
| Health / resources | Top-left or bottom-left (bottom-centre in some action games) |
| Minimap | Top-right or bottom-right corner |
| Ability / hotbar | Bottom-centre |
| Quest / objective tracker | Right side, below the minimap |
| Notifications / loot feed | Side edge, never the centre |
| Pause / settings / menu | Top-right corner (touch) or Esc / Start |
| Close button (window) | Top-right of that window |
| Back | Top-left (touch) or B / Esc / right-click, the same everywhere |
| Confirm / Cancel pair | Same order on every screen; primary on the side the platform expects (right on PC) |
| Currency in a shop | Top of the shop, visible while buying |
| Item details | Next to the selected item or a fixed side panel, never a random spot |

### Principles

- **Proximity:** what belongs together sits together; a button acts on the thing closest to it.
- **Action next to its subject:** "Equip" next to the item, not in a distant toolbar.
- **Reading flow:** top-left → right → down. Title and context top-left, primary action bottom-right.
- **Fitts's law:** frequent actions large and near the cursor/thumb/focus. Edges and corners are easy
  to hit with a mouse.
- **Hick's law:** show the 3-7 most used options; the rest behind "More" or a sub-tab.
- **Stable positions:** a button never moves between screens or states. Unavailable → disabled, not
  removed (removing shifts the others).
- **Keep the centre clear** during gameplay: that is where the action is.
- **Touch:** primary actions in the thumb zones (bottom corners, bottom half); rare ones in the top
  corners; thumbs must not cover important info.
- **Gamepad:** everything reachable by D-pad/stick, focus order logical (no jumping across the
  screen), focus always visible (the theme's amber outline), prompts for the active device.

## 2a. Multi-platform

- **Same logic, adapted layout:** a feature lives in the same conceptual place on every platform;
  sizes and positions adapt to the input.
- **Input-agnostic:** UI reacts to actions (`ui_accept`, `ui_cancel`, navigation, named `InputMap`
  actions), never to raw keys (`core/input-conventions`).
- **Right prompts** for the active device, switching automatically when the input changes
  (`InputHints`).
- **No hover on touch:** essential info is never hover-only; give a tap/long-press or a details panel.
- **Design for the hardest platform first** (phone for size, VR for comfort), then check PC does
  not look oversized.
- **Mobile:** respect safe areas (notch, home indicator, rounded corners), primary actions in thumb
  reach, no tiny close buttons in top corners.

## 2b. VR

VR UI lives in 3D space; bad VR UI causes discomfort, fatigue or nausea. These rules are about
physical comfort, not looks. Project specifics: `xr/controls`, `xr/vr-action-map`.

### Placement in space

- **Distance:** main panels ~1-2 m away (~1.5 m for laser-pointer UI), never closer than ~0.5 m,
  except direct-touch UI (wrist menu, hand panels) within arm's reach (~0.4-0.6 m).
- **Height:** centred at or slightly below eye level (~10-20° down). Looking up or far sideways tires.
- **Field of view:** important content within ~30° left/right of where the player faces.
- **Wide panels curve** around the player so every part is at the same distance.
- **No head-locked UI:** world-locked panels, or panels that lazily follow the head with smoothing
  and delay. Exceptions: brief fades, critical warnings.
- **Body-anchored menus:** frequent quick actions on the wrist/hand of the non-dominant hand.
- **Recenter:** offer it, and spawn menus in front of the player's current facing, not world forward.
- **Seated and standing:** reachable and readable in both; nothing on the floor or high overhead.
- **No clipping** into world geometry: render menus on top or move them.

### Size

- Think in **visual angle**: body text ≥ ~1.5°; test in the headset, screenshots lie.
- **Laser targets** bigger than on PC (hands shake): generous padding and hit areas.
- **Direct-touch targets** ≥ ~2-3 cm, spaced so two are not pressed at once.
- No thin lines, small fonts or fine detail: they shimmer at headset resolution.

### Interaction

- **Pointer feedback:** visible ray, cursor dot on the panel, hover highlight, pressed state.
- **Haptics:** very light on hover (optional), short on press.
- **Both hands:** left- and right-handed players; let the player choose the dominant hand.
- **Press on release** (or a clear trigger pull) to avoid accidental clicks while aiming.
- **Scrolling:** thumbstick and grab-drag; never rely on tiny scrollbars.
- **No gorilla arm:** long interactions = panel lower and closer, or pointer use.
- **Text input** is slow: minimise it, offer presets or a large virtual keyboard.
- **Comfort:** no sudden large bright panels, dark/neutral backgrounds (the glass look fits), no
  flashing, never move the UI while the player reads it.
- **Opening a menu never moves the camera.** Optionally dim the world behind it.

## 3. Layout and space

- **Containers, not hand-placed positions** (`UiKit.VBox/HBox`, `MarginContainer`, anchors), unless
  there is a reason. Align to shared edges.
- **Spacing scale** 4 / 8 / 16 / 24 / 32 / 48 px; never 13 or 27.
- **Spacing shows grouping:** small gap inside a group, bigger between groups, the same relation the
  same gap everywhere.
- **Use the space:** no small box in a huge empty screen, no small list stretched full width. Size
  panels to content and purpose; lists and grids grow to fill their panel.
- **No overflow or overlap**, in every language (plan +30-40 % length). Long names: ellipsis
  (`TextOverrunBehavior`) + tooltip, or wrap.
- **Margins / safe area:** ~3-5 % of the screen from the edges (the HUD gutter is 16 px).
- **Responsive:** anchor to the right corner/edge; test at the smallest and largest supported sizes.
- **Hierarchy:** one focal point per screen, by size, weight, colour and position; not everything
  bold and bright.
- **Density matches context:** an inventory may be dense; a racing HUD is sparse.
- **Empty states:** an empty list says something helpful, never a blank box.

## 4. Size coherence

Tokens are defined once (`UiTheme`) and reused; never a size ad hoc.

- **Button tiers:** primary large (~56-64 px tall), secondary medium (~44-48 px), tertiary/icon small
  but ≥ the minimum hit target.
- **Minimum hit target:** ~44x44 pt (iOS) / 48x48 dp (Android) on touch, ≥ 32 px with a mouse. The
  clickable area may be bigger than the visual.
- **Same role = same size:** every Close button identical, every list row and item slot the same.
- **Importance = size:** a rare option is never bigger than the main action.
- **Type scale:** the `UiTheme.Font*` sizes; body readable at the viewing distance (TV/couch needs
  bigger, ~28 px body at 1080p).
- **Icons:** a small set of sizes (16 / 24 / 32 / 48 / 64) in one stroke weight (`Ui/Icons`).
- **Corner radius, borders, shadows:** one set for the whole game (`GlassPanel` defaults, `style-guide`).

## 5. Consistency

- Same action = same icon, word, colour, position and input everywhere.
- Same kind of element = same look; interactive things look clickable, others don't.
- Reuse `UiKit`/`Modal`/`Radial` components instead of a near-duplicate; if something new is needed,
  match the existing UI first.
- One name per concept ("Inventory", never sometimes "Bag").

## 6. Feedback and states

- Every interactive element has **normal / hover-focus / pressed / disabled / selected**.
- Disabled says *why* ("Requires level 5", "Not enough money").
- Feedback within ~100 ms (visual, sound or animation); longer actions show progress.
- Confirm success, explain failures clearly.
- UI animations ~100-300 ms, never block input, skippable.

## 7. Navigation

- Important things ≤ 2-3 levels deep.
- The player always knows **where they are** (title, highlighted tab) and **how to go back** (the
  same Back input everywhere).
- Remember the last tab / item / scroll position on return.
- Tabs switch with the shoulder buttons (pad) and Q/E or number keys (keyboard) where it fits.
- Opening a menu focuses the most likely next choice (e.g. "Continue" when there is something to
  continue).
- Keyboard shortcuts for frequent actions on PC, shown in tooltips.

## 8. Error prevention and safety

- Destructive actions are separated from common ones, in the warning colour (`UiTheme.Bad`), and
  confirmed (`Modal.Confirm`) or undoable.
- Confirmations say what happens: "Delete save 'Slot 2'?" with "Delete" / "Keep", not "OK" / "Cancel".
- Never put a destructive button where a frequent one usually is.
- Prevent invalid input instead of punishing it (disable "Buy" when unaffordable, clamp fields).
- A UI action never loses progress or input (closing a window, a popup appearing).
- Popups never steal gameplay input (a dialog appearing while the player holds a button and
  confirming it by accident).

## 9. Readability and accessibility

- Text contrast ≥ 4.5:1; over busy scenes use a backing panel or the outline + shadow of
  `style-guide` (floating text).
- **Never colour alone** to carry information: add an icon, shape or text; mind red/green.
- UI scale and text size options where possible.
- Remappable controls; no hold or rapid-press without an alternative.
- Subtitles readable (size, background, speaker name).
- No tiny or thin low-contrast fonts, no walls of text: short sentences and icons.

## 10. HUD

- Show info **when it matters**, hide it otherwise (`style-guide`: nothing on screen unless in use).
- Critical info (low health, danger) is noticeable from peripheral vision: screen-edge effects,
  pulsing, sound.
- HUD in the corners and edges; the play area stays readable.
- Often-changing numbers don't jitter the layout (fixed-width digits or fixed containers).
- Hints appear near what they explain, when they are needed, and can be dismissed (`tutorial`).
- Opening a menu in single-player pauses or protects the game state.

## 11. Anti-patterns

- Buttons put where there was free space instead of where the player expects them.
- Primary action small or hidden while a rare action is big.
- Confirm/Cancel order changing between screens.
- A small content box floating in a huge panel, or content touching the panel edges.
- Several button sizes / fonts / paddings differing for no reason.
- Close buttons in different corners on different windows.
- Tooltips covering what is hovered or going off-screen.
- Text overflowing its box, especially after translation.
- Lists with no sign there is more to scroll.
- A popup appearing under the cursor and clicked by accident.
- Information needed for a decision (price, stats, money owned) on another screen than the decision.

## Self-review checklist (before finishing any UI task)

If any answer is "no", fix it or say why in the PR.

**Player logic**
- [ ] I can state the screen's main goal, and its most important action is the most visible.
- [ ] Every element is where a player would look first (convention or next to related content).
- [ ] Frequent actions are large and close; rare/destructive ones are separated.
- [ ] The info needed for a decision is visible at the moment of the decision.

**Layout and space**
- [ ] Everything aligns and uses the spacing scale.
- [ ] Groups are clear; no awkward empty or cramped areas.
- [ ] Works at every supported resolution/aspect ratio and inside the safe areas.
- [ ] No text overflow, longer translations included.

**Size and consistency**
- [ ] Sizes come from `UiTheme` tokens; same role = same size.
- [ ] Hit targets meet the minimum for each input.
- [ ] Icons, words, colours and positions match the rest of the game.

**Interaction**
- [ ] All states exist (hover/focus, pressed, disabled with a reason, selected).
- [ ] Fully usable with mouse, keyboard, gamepad, touch and VR, focus order logical.
- [ ] Back/close behaves as everywhere else.
- [ ] Every action gives feedback; destructive ones are protected.

**Readability**
- [ ] Contrast holds over a bright scene (snow, sky) and a dark one.
- [ ] Colour is never the only signal.

**Platforms**
- [ ] Right on PC, phone and tablet (touch, iOS and Android) and VR.
- [ ] No essential info is hover-only.
- [ ] Mobile: inside safe areas, primary actions in thumb reach.
- [ ] VR: comfortable distance and height, not head-locked, readable text, large pointer targets,
      haptic + visual feedback, left- and right-handed.

When finished, mention any UX trade-off made or concern noticed in the existing UI, even outside
the task's scope.
