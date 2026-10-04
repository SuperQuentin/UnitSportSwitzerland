# VR controls: Touch controllers as a virtual pad (#186)

The full per-action design and rules: `xr/vr-action-map`.

- **How it works.** `XR/XrPad` replays the controllers as joypad events (`Input.ParseInputEvent`,
  device 7). Every `PlayerInput` action is bound to "any device", so all of them work unchanged.
  - `PlayerInput._Input` pins `LastDevice` to Gamepad in VR (pad-only behaviour); prompts ask
    `PlayerInput.HintDevice`, which is VR, and name the real controller (#435, `core/key-hints`).
    `XrPad.Control` is the reverse of the layout below: change the two together.
  - `PlayerInput.Rumble` routes to `XrSession.Rumble`, which pulses both hands.
- **Layout.**
  - Left stick: move. On foot it is rotated to the **head's** yaw, so forward is where you look.
  - A, B, X, Y: the pad's A, B, X, Y.
  - L3: sprint.
  - Right stick on foot: X snap turn, up D-pad up (emote wheel, the hammer's turn), down D-pad
    right (next item). Mounted, where the head is the look, it is the whole D-pad (#436): up
    engine, right lights, left roof / horn / couple / speedbrake, down tune. Only the stronger
    axis counts, so a diagonal presses one direction.
  - R3 tap: view (cockpit body). R3 hold: recentre.
  - Left menu tap: Start. Left menu hold: Back (inventory).
- **Triggers and grips.**
  - On foot, the triggers act as **shoulders**: right = use item / fire, left = aim.
  - Mounted, they act as **triggers**: throttle, and brake / plough.
  - Mounted, the grips are the shoulders (trick, boost, shift paddles, flaps), except while a grip
    holds something (below): `XrPad.LeftGripBusy` / `RightGripBusy` mute it until it opens.
  - On foot the grips only grab (#437): use and aim are the triggers' alone.
- **Crouch (#437).** On foot, the head more than 0.35 m below where it was calibrated presses B
  (slide while running, dive while swimming), never while a menu is open (B is back there).
- **Hands (#243, `XR/XrHands`).** A grip closing (> 0.7, opens < 0.35) is the hand closing.
  - **Steering wheel**, first person in the driver's seat (car, truck, bus): a hand within 0.14 m of
    the rim catches it; its marker snaps onto the rim and rides round with it. The hands' turn about
    the column (in the column's frame, so the vehicle turning does not count) accumulates into an
    absolute, multi-turn angle clamped to ±`WheelLock`/2, sent as `XrSession.WheelAngle` and merged
    in `FootPlayer.RidePhysics` as `RideInput.WheelAngle` (the #68 real-wheel channel). Two hands:
    the mean of their turns. A hand lets go when the grip opens or when pulled > 0.22 m off its
    point; with no hand on, `WheelAngle` is NaN and the sticks steer (the rack self-centres).
    Haptic ticks on grab, every 0.4 rad, and on a slip. Rigs expose `SteeringGrip` (node, column
    axis, rim radius).
  - **Doors**, on foot: a grip closing with the hand within 0.6 m of a car door's middle
    (`FootPlayer.TryToggleCarDoor(hand)`) or 0.7 m of a building doorway, between sill and lintel
    (`InteriorManager.TryDoorByHand`), toggles it through the usual server-checked paths. A toggle,
    not a hand-driven swing: door state is binary on the network.
  - **Things on the ground** (#437), on foot: a grip closing within 0.3 m of a dropped item picks it
    up (`ItemController.PickUp`), of a radio opens its panel, as E on the thing pointed at does.
  - **Reaching out** (#437), on foot: a grip closing with the hand over 0.45 m from the eyes and in
    front does what E does there (`FootPlayer.TryInteract(byHand: true)`): a seat, a ladder, a
    crate, a cupboard, a car's or a building's door. Never the dance, which is not a thing.
  - **Hip hotbar** (#437): a grip closing more than 0.75 m below the eyes and 0.12 m to a side
    taps `next_item` (right hip) or `prev_item` (left hip).
  - **Radial wheels aimed by hand** (#437): the emote wheel reads `XrSession.HandAim`, the right
    hand's move across the view since the wheel opened (0.15 m = full), when the stick is idle.
  - **Fling to drop** (#437): a grip squeezed on nothing and let go with the hand moving over
    2.5 m/s in the play space (walking does not count) taps `drop_item`.
- **Cab and cockpit controls (#438, `XR/XrCabControls`).** Knobs in the driver's eye frame (the
  rig's anchor before the head is written back), drawn in the headset only (amber: grip, blue:
  poke). A grip closing within 0.09 m holds one; it takes that grip from the hands and the pad.
  - Spring levers tap an action a notch at a time along their axis and spring back: truck
    sequential lever (back up, forward down), retarder stalk (down more, up less), airliner flaps
    (back more, forward less), speedbrake (each pull back a step), landing gear (either way).
  - Hold levers hold an action while pushed: airliner trim wheel, steamer whistle cord.
  - The H-pattern lever (trucks set to H-pattern) taps the gear of the gate it sits in
    (`XrControlNames.GateOf`, unit tested): 1 3 5 ahead, 2 4 6 back, reverse left of 1.
  - Pokes, the tip within 3.5 cm: bus kneel and destination, airliner parking brake and
    autopilot, car radio previous / next / panel.
  - Places are guesses at each dash, not its drawn switches. `--xrcab truck-h-bus|truck-seq|car|airliner|steamer`
    builds a set round any view, for checks with `--xrsim`.
- **Flying with the arms (#438, `XrRig.BodyFlight`)**, added to the left stick: the pigeon flaps
  when both hands beat down faster than 1.6 m/s; the wingsuit, hands over 1 m apart, rolls toward
  the lower hand; under a canopy each hand pulled down past the shoulder is a brake (one turns,
  both slow and flare).
- **Wrist menu (#437, `XR/XrWristMenu`).** The back of the left wrist (the controller's +X) turned
  toward the eyes, in view within 24° and 0.7 m, for 0.6 s opens a menu on the UI panel: travel,
  inventory, map, bird journal, drop the held item (on foot), fly camera / walk, controls,
  recentre. A pick taps the action (`XrPad.Tap`) after the menu has let the pointer go, so the
  screen it opens is the one the key opens. In the world only (`XrRig.InWorld`), never on the
  title. Prompts for an action with no controller input but a wrist entry say **Wrist**
  (`XrWristMenu.Reaches`). `--xrwrist [Entry,Entry…]` opens it (and picks those, 2 s apart) for
  checks with `--xrsim`.
- **Pigeon (#217).** As a pigeon the triggers stay shoulders (right = drop), A flaps, B dives, the eye is
  the bird's (level), snap turn works perched or walking (`player/pigeon`).
- **Analog triggers (#436).** Parsed events do not set `Input.GetJoyAxis`, so code that wants a
  trigger's pull reads the pad-only actions `PlayerInput.TriggerRight` / `TriggerLeft` (flight's
  lever and climb, the GPX camera) and the garage reads `look_left` / `look_right`. In an aircraft
  the VR triggers climb and descend, analog, as a pad's do.
- **UI.** `XR/XrUi` draws the game's UI on a 1.5 m panel 1.7 m ahead. The panel follows the head's
  yaw lazily and has no depth test.
  - It draws every `CanvasLayer` by attaching the layer's canvas to a SubViewport in the
    RenderingServer. It does not use `CustomViewport`: set on a layer already in the tree, that
    logs disconnect errors when the layer exits.
  - It also draws the root canvas, through a shared `World2D`.
  - `FeelScreen` and `LensLayer` are left off the panel.
  - It is in the menu style (`ui/style-guide`): the beam is a faint white line, and an amber
    reticle sits on the panel, filling to full amber while the trigger is held. The panel ignores
    fog (`DisableFog`). The pointer only shows while the hand is tracked.
  - The controller markers use the menus' dark glass, with an amber tip.
  - Input stays with the root viewport. The panel is the root's size, so the right hand's ray
    becomes mouse events at that canvas point, and the hand's trigger clicks. This only happens
    while the mouse is not captured, i.e. while a menu is open.
