# Travel menu (R) and controls screen (F1) (#210)

- **Travel menu** (`Player/RideUi`): the menus' glass panel sized to the window (min(view - 48, 1240x780)),
  tabs Mounts / Cars / Motorbikes / Trucks and buses / Trailers (Tab / Shift+Tab, LB / RB, click), a
  scrolling card grid whose column count follows the width (`Fit`, on `SizeChanged`), and a side column:
  live stage, name, blurb, car preset (Cars) or load (trucks, trailers), refusal status, Ride button.
  Click a card or 1-9 (the current tab's first nine) rides; R / E / Esc / B close. Opens on the tab of
  what you ride. A mouse never auto-focuses a card (focus would "point" at the first card unasked); a pad does.
- **Thumbnails** (`Player/RideStage.cs`: `RideThumbs`): each card's model (`Rideable.Create(kind).BuildParkedVisual(0)`,
  `HeavyRig.Create`/`CreateTrailer`, a standing figure for On foot) rendered once on a transparent `RideStage`
  (own `World3D`, 288x176, MSAA), 3 frames each, the open tab first, the rest of the roster after.
  Cached in memory and as `user://thumbs/<fnv>.png`, keyed by kind + the catalog spec's `ToString()` + a
  `Version` const: **bump `RideThumbs.Version`** when the framing or lighting changes, a changed spec
  re-renders by itself. Thumbs fade in as they land.
- **Live stage** (`RideStage`, live = floor disc, shadows, glow, backdrop): the card under the pointer or the
  pad focus goes on it; after 0.12 s `Juice = 1` opens every door (`CarRig.DoorsOpen`, `HeavyRig.DoorsOpen`,
  trailer sections too), turns the headlamps on (pop-ups rise) and the tail lamps, and fades in two
  `SpotLight3D`s at the nose for pools on the floor (a short flicker as they catch). While pointed it swings to
  `PresentYaw` (face and driver's door to you); unpointed the doors shut, lamps go out and it turntables
  slowly. Drag on the stage to turn it. Motorbikes and mounts have no doors or lamps API: they just turn.
- **Framing**: the camera distance is the least that keeps the bounds' 8 corners in frame (exact
  perspective fit), the live stage fits the circle the subject sweeps. Bounds must include the root
  node (a figure is a single `MeshInstance3D`): missing it framed a default 1 m box and cropped riders.
- **Controls (F1)** (`Core/ControlsHelp`): same glass panel sized to the window; groups (the `Groups` data,
  unchanged) as cards in 1-4 balanced columns of >= 320 px, scrolling past the height; each binding a key cap
  chip (keyboard amber, pad blue, a bare dash where there is none).
- Screenshots: `--ride foot,12,out.png --ridemenu <tab> <card>` (0-based; points at that card),
  `--ride foot,10,out.png --controls`.
