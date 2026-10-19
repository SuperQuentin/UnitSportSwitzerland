# Foldable smartphone (#889)

Status: **built** (PR #890), VR a gap. One PR, built in phases.

| Phase | State | What |
|---|---|---|
| 1 Model | done | Book-fold phone mesh: two halves on a hinge, cover screen, inner screen split over both halves; `[Showcase("Items")]` folded / half / open |
| 2 Take out, put away | done | `PhoneState` (0 pocket, 1 folded, 2 open) replicated; arm poses `Phone` / `PhoneOpen`; the phone in the right hand on every peer |
| 3 Camera | done | Third person: a `PhoneView` blend over the shoulder (folded), then onto the inner screen until it covers the view (open). First person: the phone in camera space, lower right, rising to fill the view |
| 4 UI | done | Cover screen (clock, newest call, quick actions); inner screen home + apps in one `SubViewport`, shown on the mesh and, once covered, full screen (input pushed into it) |
| 5 Apps | done | Map (search + travel), Contacts (online players: go to, bring here, party, fight), Activities (opens the existing screen) |
| 6 Hidden travel | done | Teleport while covered, wait for the ground (`Teleporter.Arrived`), "Travelling…", then fold and pull the camera back at the new place |
| 7 Calls and sounds | done | Incoming invite rings / buzzes, answer or decline from the cover; `SfxBank` ringtone, buzz, hinge, tap |
| 8 Devices, checks, notes | done, VR gap | Keyboard / pad / VR rows, `--phonecheck`, notes |

## Built differently from the plan

- First person does not draw the phone in camera space: it borrows third person, as a throw does, and
  takes the same over-the-shoulder camera.
- The shoulder camera stands high over the shoulder (0.6 m above the phone, 0.42 m out) at a 32° FOV:
  closer in, the head and the hair filled half the picture.
- The body is hidden from the lens once it leaves for the inner screen (it passes the face); the
  phone stays drawn.
- The cover screen shows one quick action at a time (‹ › and dots), readable at its size.
- A teleport asked while the phone is folded (an answered call) opens it first, then folds it away
  at the new place; in the pocket, teleports are unchanged.
- VR: refused for now (a gap in `xr/vr-action-map`).

## Goal

One object that is both the player's menu for "do something with someone / somewhere" and a
camera trick: while its screen covers the view, the world behind can change (a teleport, an
activity starting elsewhere) without the player seeing anything jump.

Out of scope: voice calls (there is no voice chat), messages, photos app, flashlight, phone in a
vehicle (phase 1 is on foot only), a persistent contact list.

## States and controls

| State | Keyboard | Pad | Body |
|---|---|---|---|
| Pocket → folded | `phone` (Up arrow) | hold Back → quick menu wedge "Phone" | stops; right hand brings it to the chest |
| Folded: pick | Left / Right, mouse wheel | D-pad / stick left-right | |
| Folded: run | Enter, Space, left click | A | |
| Folded → open | Up arrow | D-pad up | both hands, hinge opens |
| Open → folded / pocket | Down arrow, Esc | B | |

- While the phone is out it registers with `UiFocus`, so the body does not walk and items do not fire.
- Pad on foot has no free button (D-pad up is the emote wheel, #404), so the phone is a quick-menu wedge.
- **VR**: no camera moves in VR (#186). The wrist menu's "Phone" entry puts the open phone in the
  left hand as a panel, poked with the right ray (`XrUi`); travel goes behind `XrRig.Blink`.
  Gap in phase 1 if not built; row in `xr/vr-action-map` either way.

## Model (phase 1)

- Book fold, hinge on the left long edge. Folded 0.075 x 0.15 x 0.012 m; open 0.15 x 0.15 (a square inner screen).
- Two halves: the back half (in the hand, inner screen right part) and the front half (cover screen
  outside, inner screen left part) rotating about the hinge, 0° folded to 180° open.
- Screens are separate surfaces with their own material so the UI texture is set on them; remote
  copies show a lit wallpaper only.

## Camera (phase 3)

- `FootPlayer.PhoneView` (0..1 folded) and `PhoneOpen` (0..1 open), set every frame by the phone
  controller like `ThrowAim`; the camera code eases them.
- Third person, folded: the throw camera's close shoulder, lower and looking down at the hand.
- Third person, open: the camera interpolates (smoothstep) from the shoulder view to a pose on the
  inner screen's normal, at the distance where the screen covers the view at a narrowed FOV.
  `Near` drops to 0.02 m while the phone is out.
- First person: the phone is drawn in camera space (lower right), and opening moves it to the
  covering pose in front of the lens.

## UI (phase 4)

- One `SubViewport` per screen. The inner one is square; its content lives in a centred band of the
  game window's aspect, with wallpaper above and below. When the 3D screen covers the view exactly
  that band is what is seen, so the full-screen overlay (`TextureRect`, keep aspect covered, input
  pushed into the viewport with `PushInput`) is the same picture, pixel for pixel.
- The viewports update only while the phone is out.

## Hidden travel (phase 6)

- `Teleporter.Arrived` (new): raised by `SpawnPoint` once the body stands on streamed ground.
- The phone, if open when a teleport starts (its own Map app, or a server `ForceTeleport` from an
  accepted call or an activity), shows "Travelling to X…" until `Arrived` plus a short settle, then
  folds and goes back in the pocket: the camera pulls back from the screen at the new place.
- Online, Map travel obeys `Permissions.CanTeleport`; without it the app points to Contacts (ask to join a player).

## Bots

Nothing new for bots: the phone is presentation over actions that already have server paths
(invites, teleport requests, activities). A row in `bots/bot-capability-map` says so.
