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
  - The grips are the shoulders (trick, boost, items), except while a grip holds something
    (below): `XrPad.LeftGripBusy` / `RightGripBusy` mute it until it opens.
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
