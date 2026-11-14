# Phone: a foldable in the hands, its screen as the curtain for travel (#889)

- **What**: every player carries a book-fold phone (`src/Phone/`). The first press takes it out folded, its narrow
  cover screen has quick actions; the second opens it on the last app used (Home the first time), and the body
  walks the whole time (#1825: "Folded first, walking everywhere" at the end of this note says how, and replaces
  the stand-still, dolly-square-on and right-click-is-Back statements below). The inner screen's apps take the input: Map (search, travel), Contacts (online players: go to, bring
  here, party, fight; incoming calls = invites), Activities (opens the existing screen over the phone).
- **Controls**:

  | | Keyboard | Pad | VR |
  |---|---|---|---|
  | take out (again: open, again: put away) | `phone` = Y, a toggle (#1825; was Up, the excavator's boom in a cab) | quick menu (hold Back) › Phone | grip at the left front pocket; wrist menu › Phone |
  | folded: pick / run | ← → or the wheel / Enter, a digit 1-6, click | D-pad ← →, / A | swipe the fingertip across the cover / tap it, or point the right hand's ray and pull its trigger |
  | open (on the last app) | Up | D-pad ↑ | the free hand swings the front half open by its loose edge; X (fold again: X) |
  | back / put away | Esc, Backspace, mouse button 4 (open: back, then fold; ↓ folded), hold Esc, a sprint start | B, hold B | B, or a swipe right; let go at the pocket (anywhere else it flies home) |
  | camera app: shoot / selfie / zoom | click, Enter / F / wheel | RT / Y / LB RB | the holding hand's trigger / turn the phone's back to your face / the zoom chips on the glass (1×, 2×, 5×) |
  | travel / answer a call | automatic / cover › Answer | same | raise the phone to your eyes (a blink after 3 s) / to your ear |

  No pad button is free on foot (D-pad ↑ is the emote wheel), so the pad takes it out from the quick
  menu. While out the body walks (#1825) but items do not fire (`ItemController.UsablePlayer`
  is null, `HeldItemId` reads 0). VR (#920, `XR/XrPhone`, plan `docs/plans/phone-vr.md`): held in the real
  left hand (right when left-handed; the aim nodes are already swapped, so the holding controller is
  always `_left`, the grip poses are the real hands), the other index tip is the pointer (ring within
  3 cm, touch at 6 mm, release past 12 mm; a tick at both), the body still walks. The free hand on the
  front half's loose edge drives `PhoneController.VrHinge` (the fold is the hand's angle about the
  hinge, through the inverse smoothstep). The camera app's lens is the phone's, not levelled; the
  phone's back to the face for 0.3 s is a selfie. A travel waits for the screen within 14 cm of the
  eyes, else blinks after 3 s; a call buzzes the holding hand twice and rings at the pocket, the cover
  within 10 cm of an ear answers. Let go away from the pocket it hangs 0.5 s and flies home. The
  inner screen is laid out for 640 units (3 × 2 home tiles). No camera moves in VR (#186: `PhoneLook`
  must not raise the throw view there). Away from the glass, the right hand's aim ray (the menus'
  faint beam, the screen's ring as its reticle) with its trigger is the pointer; off the phone it does nothing
  (since #1253 the camera zoom is the chips). Check: `--phonevrcheck --xrsim --world fixture` (scripted
  hands: `XrPhone.Sim*`, a ringing call: `PhoneController.SimCall`).
- **Wire**: one byte, `FootPlayer.PhoneState` (0 pocket, 1 folded, 2 open), on change, in
  `OnChangeProperties` and the movie recorder (`ActorIo`, index 23). Every peer derives the arm pose
  (`ItemArmPose.Phone` / `PhoneOpen`, both hands; switching between the two does not ease out first)
  and eases the hinge from it; a remote copy's screens show the wallpaper only.
- **Model** (`PhoneModel`, `[Showcase("Items", "Phone")]` folded / half / open): origin at the back
  half's centre, +Z out of the screens, hinge on the holder's left; the front half turns about it by
  `-π·smoothstep(fold)`. Placed in `FootPlayer.PhoneInHands` from `HandLocal`, turned to the eyes,
  7.5 cm toward them (in front of the fingers).
- **Camera** (superseded for the folded and open views by #1825, below; the travel curtain, the touch screen and
  the camera app still use these) (`FootPlayer.Phone.cs`, owner only, after the normal camera): `PhoneLook` eases from the
  normal lens to one high over the right shoulder (0.6 m up, 0.42 out, 0.2 back of the phone, by the
  *body's* facing, not the phone's), its up the phone's long side, FOV 32°; `PhoneCover` eases on to
  square in front of the inner screen at the distance where the screen's width (wide window) or height
  (tall one) fills the view. Near drops to 0.01 m; near and FOV are restored from the saved values
  (set each frame from them, not eased from the last, which the game's FOV easing would fight).
  From first person it borrows third person as a throw does (`StepThrowView`). Past `cover > 0.12`
  the body is seen from the eye (shadow only): the lens passes the face.
- **Full-screen hand-over** (now only the Travel curtain and touch, #1825): the inner `SubViewport` is square, as wide as the window; its content is a
  centred band of the window's shape (`PhoneScreens.Fit`). At full cover the 3D screen shows exactly
  that band, so the overlay (`TextureRect`, keep aspect covered, layer 30) is the same picture; it fades
  in over 0.15 s to hide the tonemap's colour step. Input goes to the viewport with `PushInput`, mouse
  positions mapped through the cover fit (`PhoneController.ToScreen`).
- **Gotcha**: a `Control` anchored full-rect straight under a `SubViewport` came out at twice the size
  (the window's content scale): the screens' roots are sized by hand.
- **Travel behind the screen**: `Teleporter.Curtain` (set by the phone) is asked first by every
  `TeleportTo`, the server's `ForceTeleport` too (accepted calls, activities). Phone out: the jump waits
  until the screen covers the view (runs at once if open), the screen shows "Travelling to X", and once
  `Teleporter.Arrived` (raised by `SpawnPoint` when it settles) and the body is on the floor for 0.9 s,
  the phone folds and goes back in the pocket, the camera pulling back at the new place. In the pocket,
  a teleport goes as it always did. Online, Map travel obeys `Permissions.CanTeleport`.
- **Sounds** (`PhoneSounds`, in the sound player): draw rustle, hinge slide + click, tap, and a
  pocket buzz on an incoming invite (the toast keeps its chime).
- **Bots**: nothing new: the phone is UI over actions with server paths (invites, teleport requests,
  activities); `bots/bot-capability-map` says n/a.
- **Check**: `--phonecheck --world fixture [--view third]` (quick tier): out, quick pick, open, travel
  while open and while folded (held until covered), Down puts it away; windowed it writes
  `test_output/phone_*.png` and the cover screen's own picture.

## Since the iPhone Duo pass

- **Model**: after the iPhone Duo: halves 8.4 x 12.2 cm, open 16.8 x 12.2 (inner screen 1.4 : 1), rounded
  outer corners, square hinge corners (no notch at the fold), silver rim, white back with a pill camera
  island (two lenses, flash), cover screen edge to edge with a punch hole (drawn in its UI). Built with
  `SurfaceTool` rounded-rect extrusions (`PhoneModel.Builder`), not `MeshScratch` boxes. Fully open, the
  inner screen is **one mesh** (`InnerFull`), so there is no seam; the two half screens show only while it moves.
- **Open = still a thing in the hands**: no 2D overlay any more. The lens stops where the whole phone fits
  with 10 % room (`PhoneScreenView`), a dark sheet on the lens just beyond the phone hides the world
  (`PhoneBackdrop`, eased; a held teleport runs only once `PhoneBackdropShown` is 1). The mouse goes to
  the screen through a ray onto the 3D screen (`FootPlayer.PhoneScreenPoint`).
- **Sway**: the lens follows a smoothed copy of the phone (`_phoneSeen`), so steps and turns show as the
  phone moving; the owner's phone breathes a little and leans up to ~3° toward the cursor.
- **Walking**: see "Folded first, walking everywhere (#1825)": the body walks in every state.
- **Camera app** (`Page.Camera`, `PhoneState` 3): the arms lift the open phone before the face
  (`ItemArmPose.PhoneRaised`, blend 1..2 over the open pose, `HumanMeshBuilder.ApplyArms`), the backdrop
  goes, the mouse looks (the phone tips with the pitch), the screen shows the back lens (`PhoneScreens.Lens`
  in its own `SubViewport`, drawn only while the app is open, 1× 2× 5×). Click / Enter / RT shoots through
  `PhotoCapture.Render` into the photo album (`PhotoStore.Save`, the PNG beside it; `--photo-dir` keeps a
  check's shots out of the real album).
- **Gotcha**: the main viewport's visible rect is in the stretched canvas's units (1280 x 696 here), not
  pixels: size render targets from `GetWindow().Size`. The inner screen is laid out on an 800-unit-high
  canvas scaled to its pixels.

## Hands on the phone (the second playtest pass)

- **The body stood while the phone was out** (until #1825, which replaced all of this with "the phone never
  stops the body"): `UiFocus` from taking it out to putting it away, the
  cursor shows. Every key was the phone's: folded, W / Up open, A D / Left Right pick, Enter / Space / E
  run, S / Down / Esc put away; open, WASD and the arrows were `ui_*` into the screen (typed as letters
  while a `LineEdit` has the keys), Enter / Space / E press, Esc goes back. The camera app turns the
  view itself from the mouse (`LookYaw` / `LookPitch`, the game's 0.0022 rate over the zoom): the body's
  own look is off under `UiFocus`.
- **The mouse swipes**: a press and release in place is a click on the screen under it; a drag past 14
  px is a swipe: up and down scroll the list under its start (`PhoneScreens.ScrollAt`), a swipe right
  goes back, on the cover left and right pick. `PhoneScreenPoint(window, out cover)` hits the cover
  folded and the inner screen open, unclamped ("a bit around" the screen).
- **The finger**: the free hand (the figure's left, rig R) points its index at the cursor's point,
  1.6 cm off the glass, down to 2 mm on a press (`FootPlayer.PhonePointTip` / `PhonePointDir`, owner only,
  never sent). `HumanMeshBuilder.ApplyPoint` puts the wrist back from the tip along the way the finger
  comes in (into the screen, up it and to the right: from in front, not along the forearm from the
  shoulder, which is behind the screen) and the hand bends there (`Rig.PointDirR`); `Hand(point)` draws
  the index straight down the hand's middle to exactly that tip, the others curled. WASD plays a 0.24 s
  swipe of the finger across the screen. Keyed to 4 mm in `FootPoseKey.Point`.
- **Views** (until #1825: now no app goes square on, see the end of this note): folded, high over the shoulder; open on an app, the same a third closer onto the open
  phone, the hand working it in view; **only the map** (and Travelling) went square on: the lens closes
  in first (`PhoneCover`), then the world fades to black behind it over 0.4 s (`PhoneBackdrop` set only
  once the cover is 1); a held teleport waits for the black. The **camera app** puts the lens behind the
  raised phone at the eyes (`PhoneCameraApp`, FOV 52°), the phone held upright, level with the horizon
  (its facing flattened, then the view's pitch), the world round it.
- **Activities app** (`Page.Activities`): the Battle Royale lobby with Join / Leave and the races on the
  server with Join / Leave (`RaceManager.RequestRows` once a second, rebuilt on change), "Start a 2 km
  race here"; the **Menu** app still opens the full Activities screen.

## Walking with the camera app (#1149)

- Once the phone is **open** on the camera app (`Stage.Open`, not while it opens), `Grip` drops
  `UiFocus`: WASD / the left stick walk, Space / A jump (held, read by the body), the right stick looks;
  the body squares to the view (the phone's `_throwBlend`). The phone keeps every event in
  `CameraInput`: shutter on click / Enter / RT (no longer Space, E, A), zoom on the wheel, ↑ ↓, LB RB
  (no longer W S). Folded and on every other app the body stands as before.

## Photos, wallpapers, selfies (the third pass)

- **Keys lead the finger**: WASD / the arrows no longer swipe: the finger hovers over what is selected
  (the inner screen's focus, `PhoneScreens.FocusUv`; folded, the quick-action card, `CoverCardUv`) and taps
  it on Enter. A mouse move hands the finger back to the cursor.
- **Photos app** (`Page.Photos` grid of the album's newest 60, `Page.Photo` one big): "Set as lock screen"
  (the cover) and "Set as home screen" (inside). `PhoneWallpaper`: the choice in `photos/phone.json`
  beside the album (`--photo-dir` moves it); a shot is shown whole from its source frame when the PNG is
  still there, else the print's picture without its border (an `AtlasTexture` on `PhotoStore.ImageRect`).
  A light shade lies over a photo wallpaper so the clock reads.
- **Zoom glides**: the lens's FOV eases to the step picked (`Damp(7)`), the shot taken at the FOV shown.
- **Selfie, the foldable's way**: in the camera app F / pad Y / the Selfie button (not the right click since #1825) turns the
  open phone half round about its middle (`PhoneState` 4, `_phoneTurn` eased on every peer, held 10 cm
  further out): the camera island and the cover screen, which faces the same way once open, face the
  player. The cover stays lit open (`PhoneModel.CoverWhenOpen`) and shows the main camera's view (the
  finder resized to the cover's shape, 80° up it); the lens sees the owner's body (`OwnBody.Layer` added to
  its cull mask). The game camera sits by the face looking at the cover (`PhoneSelfieView`). Shots go to
  the album as any.
- **The selfie pose and face**: `ItemArmPose.PhoneSelfie` (blend 1..2 over the open pose, as the raised one;
  between the two lifted poses the phone comes down to the open pose first): the phone up and out to the
  right at arm's length, the left arm loose, the head tipped toward it. The face looks into the lens and
  goes through Happy, Joy, Wink, Love, 2.5 s each, off `ClockSync.ServerNow` (`FootPlayer.SelfieFace`), so
  every peer shows the same one. The lens turns onto the head (a phone's face tracking), the phone's up kept.
- **What you see is what is taken**: a shot is `PhotoCapture.Render`'s window-sized frame cropped to the
  viewfinder's shape (`PhoneController.CropTo`: the cover's 0.68 for a selfie, the inner screen's 1.4
  otherwise), the same height of view: the saved photo is exactly the preview (`--phonecheck` checks the
  selfie's shape).


## Five verbs, stage 1 (#1253, plan `docs/plans/phone-redesign.md`)

- **Back is generic**: `PhoneController.Back` sends any page but Home and Travel up one level (Photo to Photos,
  the rest to Home); only Home folds. A new app can never fold the phone by accident.
- **Back is visible and has more ways in**: every app page has a `‹` chevron (`PhoneScreens.BackChevron`,
  pointer-only, `BackPressed`); Backspace also goes back (and since #1825 the mouse's fourth button; the right click no longer does).
- **Put away from any page**: hold Esc / the pad's B for `StowHoldSeconds` (0.4 s); a tap is still one step back.
  The page hints say so. Not while a text box has the keys, in VR (its own buttons) or on Travel.
- **Travel can be cancelled** until the jump has run: Esc / B / the Cancel button on the curtain
  (`PhoneController.CancelTravel`) drops the pending jump and shows Home; once the jump ran, the button goes.
- **Touch**: a PHONE pill in the on-foot top bar (`TouchLayout`, 9 mm, sends the `phone` action); it was only in More.
- **Not done, and why**: a hold on D-pad Up cannot take the phone out, the hold is already the emote wheel
  (`EmoteWheel`), so the pad keeps quick menu › Phone. The Phone key was Up then (since #1825 it is Y, a toggle); the way away was Esc/B held, Down folded. The persistent hint bar built from the focused control moves
  to a later stage with the Home/cover rework.
- **Check**: `--phonecheck` asserts Esc and Backspace go back, Training does not fold the phone, a held Esc puts it away, and Esc on the travel curtain cancels the jump.

## Home and cover, stage 2 (#1253)

- **Fixed cover order**: Open, Map, Camera, Contacts, Photos, Activities (`RefreshQuick`); the first thing selected
  is Open, and the digit keys 1 to 6 run a tile directly. A call no longer inserts Answer / Decline into the list.
- **A call is a banner** above the tiles (`PhoneScreens.ShowCover`, `CoverBannerUv`): Enter / A answers, Esc / B
  declines (while it rings, they are not "run the tile" and "put away"); a click or VR tap on the banner's left
  half answers, the right half declines; the tiles are still picked with arrows, digits or a click elsewhere.
- **Home has six apps** (Map, Contacts, Camera, Photos, Activities, Journal): the Menu tile (the full Activities
  screen) became **Host…** and Training a **Training** button at the top of the Activities app; Back from
  Training goes to Activities, then Home.
- **Checks**: `--phonecheck` (six tiles, Training's way back); `--phonevrcheck` selects the Camera tile before its
  tap (`PhoneController.SelectQuick`). The banner itself is not exercised headless (no invite service in the fixture).

## Map and Travel, stage 3 (#1253)

- **The Map lists places without typing** (`PhoneScreens.FillResults`): Recent (the last three travelled to this
  session, `PhoneScreens.Recents`), Near you (the four nearest places over 500 m away, `Here` from the controller),
  then the biggest places; each row shows its distance. Chips (All, Towns, Summits, Passes) narrow every section.
  The rows have the keys on open; the search box is optional (`/` or a click, `FocusSearch`) and, typed into,
  lists matches with the same chips.
- **Travel in VR is easier to start**: the curtain runs when the glass is within 40 cm of the eyes and faces them
  (`XrPhone.EyesReach` / `EyesFacing`, was 14 cm), else the blink comes after 1.5 s (`VrRaiseSeconds`, was 3 s).
- **Check**: `--phonecheck` counts the Map's rows; `--phonevrcheck` raises the phone for a travel under 1.4 s and
  holds it at the hip (`BeforeEyes(low:)`) for the blink. Recents are not saved between sessions.

## Camera, touch, stage 5 (#1253)

- **Camera app buttons**: zoom chips 1x 2x 5x with the current lens lit (`PhoneScreens.SetZoom`, gold chip), a focusable
  shutter and Selfie button. In VR the zoom is the chips only: a right trigger pulled off the phone no longer steps it
  (`VrZoom` is gone). The selfie key (F, Tab, pad Y) lives in `CameraInput`, so it means nothing on any other
  page.
- **Photos**: Left / Right and LB / RB are previous / next in the viewer (Down / Up reach its two wallpaper buttons;
  `PhoneScreens.StepPhoto`, `‹ ›` buttons and a "n / m" caption); LB / RB page the grid by a screenful of rows
  (`PageGrid`). A sideways swipe in the viewer is previous / next (mouse, finger, VR fingertip), never Back (`Swiped`).
- **Touch** (`Platform.IsTouch`, also `--mobile`): see `core/touch-controls` (millimetre slop and swipes, the square-on
  enlarged screen, camera taps). Mouse thresholds now scale with the window's height (2 % slop, 5.7 % swipe).
- **Not done**: pad A shoots when standing still (needs a definition of "still"), VR face-button handedness, the on-screen
  keyboard, tapping the glass of a *raised* phone from a real device (only the emulated mouse is checked).
- **Check**: `--phonecheck` (chips, Selfie button, F / Tab off the camera, finger tap / swipe thresholds; windowed also the
  photo pager and LB / RB), also run with `--mobile` (touch layout; windowed screenshots `test_output/phone_*_screen.png`);
  `--phonevrcheck` (stray trigger does not zoom, fingertip on the chips); `TouchLayoutTests`.

## Contacts, stage 4 (#1253)

- **One row per online player** (`PhoneScreens.FillContacts`): name and status (Online, In your party); Enter, A or a
  tap opens a sheet (`OpenSheet`) with Go to, Bring here, Party invite and Fight. Offline the page says so and
  offers Open Activities. "Call" is gone from the UI: the heading is Invites, the home tile says "Invite people".
- **Bring here and Fight confirm first** (`Confirm`): Cancel is the first and focused button, so a stray Enter sends
  nothing. Back (`PhoneScreens.CloseContactsSheet`, called first by `PhoneController.Back`) goes confirm, sheet, list,
  Home, in that order.
- **The phone sends the same chat commands as before** (`/tpa`, `/tphere`, `/party invite`, `/fight`), which is the
  path a bot uses; no new key, so pad (A / B), VR and touch (taps on rows and buttons) work as in every app.
- **A result line** under the heading stays after the action: `Fight · Anna: asking…`, then `Sent` once the
  server's Outgoing invite shows in `InviteService.Sent`, then `Accepted`, `Declined` or `Timed out`. A private
  chat line while still asking (a cooldown, a match in the way, no such player) replaces it as the reason.
- **Server feedback** (protocol 55): `InviteService.Ended` (RPC to the sender of a direct invite, from `Answer`
  and from the expiry sweep) raises the client's `Resolved(kind, name, outcome)`; the chat's "accepts / declines"
  lines are unchanged. Bots get no RPC (never a screen).
- **Checks**: `--phonecheck` (`ContactsChecks`: rows, sheet, Cancel default, nothing sent until Confirm, result
  lines; the fixture has no server, so `TestPeople` stands in and `OnResolved` plays the server's word);
  `tools/invitecheck.sh` asserts the sender is told accepted (`/tpa`, party) and declined (race duel).

## Hint line and recents (#1253 follow-up)

- **The page hint follows the focus** (`PhoneScreens.FollowFocusHint`, `AcceptVerb`): Enter / A / a tap is named for the control that has the keys ("travel to Leytron", "open Map", "search", a button's own label); the back half of the line is unchanged.
- **Map recents are saved** in `user://phone_recents.json` (LV95 coordinates of the last three, matched back to the index on load).

## Folded first, walking everywhere (#1825)

- **The rule now: the phone never stops the body.** `PhoneController.Grip` holds `UiFocus` only while a text box in
  the phone has the keys (the Map's search: letters type, WASD must not walk, the body stands) and on touch (the
  on-screen controls stay out of the enlarged screen's way, as before). Everything else, WASD, Shift, Space, the
  sticks, the mouse's look, is the body's: the phone's `_Input` (`Wanted`) consumes only the events it needs and
  lets the rest go on to `FootPlayer`. It is the camera app's mechanism (#1149) made general.
- **Two presses**: the first takes it out folded (about 0.17 s: arms 0.1 s, `LookSeconds` 0.12), the second opens it
  on the last app used this session (`PhoneController.OpenLast`, `_lastApp`; Home the first time; the cover's Open
  tile does the same) in about 0.22 s (`PhoneModel.UnfoldSeconds` 0.2, `CoverSeconds` 0.2). A digit 1-6 on the
  cover still runs its tile straight into that app. The arm blend into and out of the phone pose is 0.1 s (was
  0.18 s), the same on every peer.
- **Keys** (keyboard and mouse / pad):

  | | folded | open |
  |---|---|---|
  | walk, sprint, look | WASD, Shift, left stick; right stick | the same |
  | the cursor | none: the mouse looks as ever, the wheel zooms the camera | visible, works the screen (click, swipe, wheel) |
  | look with the mouse | always | hold the right button |
  | pick / move on the screen | ← → / D-pad ← → | arrows / D-pad |
  | press | Enter / A; digits 1-6 run a tile | Enter / A |
  | take out, open, put away | `phone` = **Y**, a toggle from anywhere (pocket, folded, open on any page, away; a Y typed in the Map search types) | the same |
  | open | Up or Enter on the cover / D-pad ↑ | |
  | back | | Esc, Backspace, the chevron, mouse button 4 / B |
  | put away | Y, Down, Esc, button 4 / D-pad ↓, B; Esc or B held 0.4 s from any page; a sprint start | Y, hold Esc / B 0.4 s; a sprint start |

  W no longer opens it and S no longer puts it away (they walk); Up and Down do. Space and E are the body's now
  (jump, use): Enter presses. **What right click lost**: it is no longer Back in the phone and no longer the selfie
  toggle in the camera app (F, Tab, pad Y and the Selfie button stay); held, it captures the mouse and turns the
  view (`StartLook` / `EndLook`: the pointer is `MouseCapture.Captured` while held, hidden, and `Input.WarpMouse`
  puts it back where it was on release).
- **A text box has the keys**: typing in the search stands the body (`UiFocus`) and nothing reaches the world;
  Esc or Back leaves it.
- **Sprint puts it away**: a NEW sprint start (`FootPlayer.SprintIntent`, the rule that holsters an item,
  `items/inventory`) puts the phone away, from any page of any state. Not on the travel curtain, not in VR.
- **Pad**: left stick walks, right stick looks, D-pad picks, A presses, B backs, hold B puts away. **A is the phone's
  while it is out**: no jump with the pad while the phone is folded or open (`FootPlayer`: the jump is dropped when
  `PhoneState` is 1 or 2 and the last device is a pad; the keyboard's Space still jumps, and in the camera app A
  still jumps). The D-pad's ↑ is consumed by the phone, so the emote wheel stays closed while it is out.
- **Views** (a viewmodel, not a camera move; rewritten after review): on desktop and pad the gameplay camera is not touched at all by a
  folded or open phone: same transform, same FOV, same third-person follow (`--phonecheck` asserts the camera equal with the phone open).
  The phone the owner reads is a second `PhoneModel` (`Viewmodel = true`, built once, moved as a node) parented to the camera on
  `HeldItemVisual.ViewmodelLayer` with `HeldItemVisual.ForView` materials (depth squeezed to the nearest 1 %, so it never clips into a wall
  or the body), `FootPlayer.StepPhoneViewmodel`: open, the inner screen 40 % of the view's width (the projected box measures 42 %),
  right edge about 2 % in, bottom about 3 % up, left edge past 55 %, so the centre and the left half stay clear; folded, the cover 15 % wide
  (39-45 % high) at 69-86 % of the width, above the hint line and left of the corner key list. Square to the picture with a 4 degree turn,
  a light sway and a walk bob; it rises from below as it comes out. The share is by width, clamped by height (`0.58` of the view open,
  `0.46` folded) so it stays in view at 4:3 and 21:9. No arm and no pointing finger on it: the cursor is the pointer open, the selection
  card folded. The in-world phone and arm poses are as before for everyone else (and the owner's third person); in first person the
  in-world copy is moved to the layer the eye never draws, so it does not show twice. `PhoneScreenPoint` / `PhoneScreenToWorld` use the
  viewmodel when it is drawn (its scaled transform), so the mouse ray hits exactly the control under the cursor. While open
  (`PhoneController.HidesHud`) the corner key list (`PromptBar`) and the hand chip hide. The lens moves only for the camera app and
  selfie, the travel curtain (square on, world black, the jump held until black as asserted) and touch; `FootPlayer.PhoneMovesLens`
  gates the third-person borrow likewise.
- **The black backdrop stays for the Travel curtain** (and the touch screen, which keeps its enlarged square-on
  view): a Map pick still holds `Teleporter.Curtain`'s jump until `PhoneBackdropShown` is 1 (`--phonecheck` asserts
  it runs only then). The body is not locked meanwhile.
- **Remote peers**: `PhoneState` is unchanged; they see the figure's gait with the folded or open arm pose
  (`--phonenet`: the copy holds the phone the whole walk, its position advances, no frame jumps it).
- **Checks**: `--phonecheck` (timings, last app, digit, walk folded / open / on the Map, held right button and its
  cursor, button 4, typing stands the body, sprint puts it away, the curtain still holds the jump; screenshots
  `test_output/phone_1825/`), `--phonevrcheck`, `tools/phonenetcheck.sh`.

### The phone key is Y, a toggle; folded the mouse looks (#1825 owner's decisions)

- **Y, not Up.** `PlayerInput.Phone` (id `phone`, unchanged so rebinds carry over) is Y. Checked: Y is bound only to `Autopilot`
  (`FootPlayer.Airliner`, an airliner's seat 0) and `Cruise` (`FootPlayer.Cruise`, gated by `HasCruise`: a car, truck, farm machine or
  motorbike driver); no on-foot, fight, sword, build, creative, spectator or movie-studio code reads Key.Y (searched the bindings and the
  users of both action names; the only other `Key.Y` is the release table). O (roof toggle) and U (radio next) were the fallbacks. In a
  vehicle the key keeps its vehicle meaning (the phone's `_Input` does not handle it there: `_player()` is null off foot, so the
  event goes on). The old clash of Up with the excavator's boom (`ArmBoomUp`) is gone with it; Up is now only the arrow: it opens a
  folded phone, moves the highlight open.
- **A full toggle**: pocket, folded, open on the last app, away. The third press works from any page, also while it opens, but not on the
  Travel curtain and not while a text box has the keys (the letter types). Hold Esc 0.4 s, a sprint start, and Down / Esc / mouse 4 on the cover
  still put it away.
- **Folded, the mouse is the view's**: no cursor, the mouse stays captured and turns the view with no button held, the wheel is the camera's
  zoom, a click does nothing on the cover. The tiles are run with a digit 1-6 or the arrows and Enter (pad: D-pad and A); the finger follows
  the selected tile. The cursor and hold-right-button-to-look apply only once it is open (`Grip`: visible while Opening or Open; folding
  or putting away captures again). Touch is unchanged (its finger plays the mouse).
- **Framing**: see "Views" above. First and third person are different pictures now (first person has no body in view and no in-world phone;
  `--view first` is needed, the check's default is third person). `--phonecheck` projects the screen's corners at 1280x720, 4:3 and 21:9
  (16:9 strict: open 38-47 % wide and left of 50 % excluded, the centre point outside the rect, cover 13-17 % wide and 38-52 % high,
  the cover clear of the key list and hand chip rects, the key list hidden open), asserts the gameplay camera equal with the phone open, and
  clicks a Home tile through real window coordinates.

## The Map app is a real map (#1826, 1B of #1824)

- **One map, embedded.** `Phone/PhoneMap` hosts the adventure map's own canvas (`AdventureMap.MapCanvasView`, now behind `IMapHost`
  and `Driven`: relief, canton borders, places, the player's arrow, the shared `IMapOverlay` layers, so pins and travel points are
  drawn) on a screen-space `CanvasLayer` (6), about 76 % x 76 % of the view at its top left (`PhoneMap.Area`), the world visible
  behind, the phone's viewmodel peeking out at the lower right. It is not on the phone's glass; the glass says "The map is over
  your view" and what is selected. Only the Travel curtain hides the world (the map gives way to it).
- **See-through, and more while you move.** The fills (background, relief, the fog) are 60 % opaque standing
  (`PhoneMap.RestAlpha`); moving (`FootPlayer.GroundSpeed` above 0.5 m/s) they ease to 20 % in 0.2 s and come back in 0.35 s once
  the body stands for 0.25 s (a `Tween`, no step). It is the canvas's `SelfModulate` (and the fog shader's `COLOR.a`), so borders,
  labels and markers stay crisp. Set only when it changes.
- **Places.** Adventure on (`AdventureLog.Adventuring`): the adventure's places and this player's fog, as on M. Adventure off: no
  fog, and the place search's places (`PlaceIndex`, towns / summits / passes by zoom, labels decluttered).
- **The old list is a search drawer** (left 38 % of the map): a box, the All / Towns / Summits / Passes chips, "Where I am", then
  Recent, Near you, Popular (`PhoneScreens.BuildRows`, shared with the glass list that VR and touch keep). A pick centres the map
  on the place (a 0.3 s glide, then the zoom step) and shows its card: name, kind, distance, **Travel** (`Teleporter.Curtain`,
  `Permissions.CanTeleport`, recents saved as before).
- **Motion** (`UiMotion`): the map springs in and shrinks out, the drawer and card likewise, the drawer's first dozen rows stagger,
  buttons squash; Reduce motion makes them plain fades.
- **Input** (desktop and pad; VR and touch keep the list on the glass, no map over the view):

  | | Keyboard + mouse | Pad | Touch / VR |
  |---|---|---|---|
  | pan | drag, arrows | D-pad (the left stick still walks) | list on the glass |
  | zoom | wheel about the cursor, `=` `-` | RB / LB | list on the glass |
  | select | click a place, Enter at the centre cross | A at the centre cross | tap / fingertip on the list |
  | travel | the card's Travel, Enter | A again on the card | the list's row |
  | centre on me | Home, the bar's "Me" | "Where I am" in the drawer | (the glass list's "Near you") |
  | search | `/`, the bar's Search | X opens the drawer, D-pad ↓ to a row, A | the glass's box |
  | back | Esc / Backspace / button 4 / ‹: drawer, then card, then the app | B, the same order | as before |
  | look | hold the right button (the map does not see the pointer meanwhile) | right stick | - |

  The body walks throughout; typing in the drawer's box stands it (`UiFocus`). The adventure's own M map is unchanged (its right
  click still places pins and its side panel still has the cards; the phone map draws the overlays but does not open overlay cards).
- **Not done:** nearby players are drawn as dots with names (`PeersOverlay`, the ones this client has) but there is no friends
  list; overlay cards (pins, travel points) are M's; the hologram is 1C (#1827); a touch pinch map.
- **Check:** `--phonecheck` (`PhoneProbe.Map`: up, 76 %, 60 % fills, arrows, drag, wheel about the cursor, Home, click, search,
  pick, Esc order, right-button look, the 20 % while walking and back, Travel behind the curtain), `--mapcheck` / `--placescheck`
  for M's map. Screenshots `test_output/phone_1826/`.
