# VR controls: Touch controllers as a virtual pad (#186)

- **How it works.** `XR/XrPad` replays the controllers as joypad events (`Input.ParseInputEvent`,
  device 7). Every `PlayerInput` action is bound to "any device", so all of them work unchanged.
  - `PlayerInput._Input` pins `LastDevice` to Gamepad in VR, so prompts show pad glyphs.
  - `PlayerInput.Rumble` routes to `XrSession.Rumble`, which pulses both hands.
- **Layout.**
  - Left stick: move. On foot it is rotated to the **head's** yaw, so forward is where you look.
  - A, B, X, Y: the pad's A, B, X, Y.
  - L3: sprint.
  - Right stick X: snap turn. Right stick up: D-pad up (engine). Right stick down: D-pad right
    (next item).
  - R3 tap: view (cockpit body). R3 hold: recentre.
  - Left menu tap: Start. Left menu hold: Back (inventory).
- **Triggers and grips.**
  - On foot, the triggers act as **shoulders**: right = use item / fire, left = aim.
  - Mounted, they act as **triggers**: throttle, and brake / plough.
  - The grips are always the shoulders (trick, boost, items).
- **Flight gap.** In `FootPlayer`, flight reads `Input.GetJoyAxis(0, Trigger*)` directly, and
  parsed events do not set that. In an aircraft, climb and descend come from A and B
  (Jump / Crouch) only.
- **UI.** `XR/XrUi` draws the game's UI on a 1.5 m panel 1.7 m ahead. The panel follows the head's
  yaw lazily and has no depth test.
  - It draws every `CanvasLayer` by attaching the layer's canvas to a SubViewport in the
    RenderingServer. It does not use `CustomViewport`: set on a layer already in the tree, that
    logs disconnect errors when the layer exits.
  - It also draws the root canvas, through a shared `World2D`.
  - `FeelScreen` and `LensLayer` are left off the panel.
  - Input stays with the root viewport. The panel is the root's size, so the right hand's ray
    becomes mouse events at that canvas point, and the hand's trigger clicks. This only happens
    while the mouse is not captured, i.e. while a menu is open.
