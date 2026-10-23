# Phone: a foldable in the hands, its screen as the curtain for travel (#889)

- **What**: every player carries a book-fold phone (`src/Phone/`). Out folded, its narrow cover screen
  has quick actions; opened, the camera dollies onto the square inner screen until it covers the view,
  and that screen's apps take the input: Map (search, travel), Contacts (online players: go to, bring
  here, party, fight; incoming calls = invites), Activities (opens the existing screen over the phone).
- **Controls**:

  | | Keyboard | Pad | VR |
  |---|---|---|---|
  | take out | `phone` = Up arrow (free on foot; the excavator's boom in a cab) | quick menu (hold Back) › Phone | grip at the left front pocket; wrist menu › Phone |
  | folded: pick / run | ← → or the wheel / Enter, Space, click | D-pad, stick / A | swipe the fingertip across the cover / tap it, or point the right hand's ray and pull its trigger |
  | open | Up | D-pad ↑ | the free hand swings the front half open by its loose edge; X (fold again: X) |
  | back / put away | Esc (open: back, then fold), ↓ folded | B | B, or a swipe right; let go at the pocket (anywhere else it flies home) |
  | camera app: shoot / selfie / zoom | click, Enter / F / wheel | RT / Y / LB RB | the holding hand's trigger / turn the phone's back to your face / the right trigger with its ray off the phone (1×, 2×, 5×, round) |
  | travel / answer a call | automatic / cover › Answer | same | raise the phone to your eyes (a blink after 3 s) / to your ear |

  No pad button is free on foot (D-pad ↑ is the emote wheel), so the pad takes it out from the quick
  menu. While out it holds `UiFocus`: the body does not walk, items do not fire (`ItemController.UsablePlayer`
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
  faint beam, the screen's ring as its reticle) with its trigger is the pointer; off the phone, in
  the camera app, that trigger steps the zoom (items are off while the phone is out, so it is free). Check: `--phonevrcheck --xrsim --world fixture` (scripted
  hands: `XrPhone.Sim*`, a ringing call: `PhoneController.SimCall`).
- **Wire**: one byte, `FootPlayer.PhoneState` (0 pocket, 1 folded, 2 open), on change, in
  `OnChangeProperties` and the movie recorder (`ActorIo`, index 23). Every peer derives the arm pose
  (`ItemArmPose.Phone` / `PhoneOpen`, both hands; switching between the two does not ease out first)
  and eases the hinge from it; a remote copy's screens show the wallpaper only.
- **Model** (`PhoneModel`, `[Showcase("Items", "Phone")]` folded / half / open): origin at the back
  half's centre, +Z out of the screens, hinge on the holder's left; the front half turns about it by
  `-π·smoothstep(fold)`. Placed in `FootPlayer.PhoneInHands` from `HandLocal`, turned to the eyes,
  7.5 cm toward them (in front of the fingers).
- **Camera** (`FootPlayer.Phone.cs`, owner only, after the normal camera): `PhoneLook` eases from the
  normal lens to one high over the right shoulder (0.6 m up, 0.42 out, 0.2 back of the phone, by the
  *body's* facing, not the phone's), its up the phone's long side, FOV 32°; `PhoneCover` eases on to
  square in front of the inner screen at the distance where the screen's width (wide window) or height
  (tall one) fills the view. Near drops to 0.01 m; near and FOV are restored from the saved values
  (set each frame from them, not eased from the last, which the game's FOV easing would fight).
  From first person it borrows third person as a throw does (`StepThrowView`). Past `cover > 0.12`
  the body is seen from the eye (shadow only): the lens passes the face.
- **Full-screen hand-over**: the inner `SubViewport` is square, as wide as the window; its content is a
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
- **Walking**: folded, the body walks and looks (no `UiFocus`); the quick actions are on the arrows,
  Enter and the mouse buttons. Open on an app, `UiFocus` and a visible cursor.
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

- **The body stands while the phone is out**: `UiFocus` from taking it out to putting it away, the
  cursor shows. Every key is the phone's: folded, W / Up open, A D / Left Right pick, Enter / Space / E
  run, S / Down / Esc put away; open, WASD and the arrows are `ui_*` into the screen (typed as letters
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
- **Views**: folded, high over the shoulder; open on an app, the same a third closer onto the open
  phone, the hand working it in view; **only the map** (and Travelling) goes square on: the lens closes
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
- **Selfie, the foldable's way**: in the camera app F / right click / pad Y / the Selfie button turns the
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

