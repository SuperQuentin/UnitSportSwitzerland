# Phone: a foldable in the hands, its screen as the curtain for travel (#889)

- **What**: every player carries a book-fold phone (`src/Phone/`). Out folded, its narrow cover screen
  has quick actions; opened, the camera dollies onto the square inner screen until it covers the view,
  and that screen's apps take the input: Map (search, travel), Contacts (online players: go to, bring
  here, party, fight; incoming calls = invites), Activities (opens the existing screen over the phone).
- **Controls**:

  | | Keyboard | Pad | VR |
  |---|---|---|---|
  | take out | `phone` = Up arrow (free on foot; the excavator's boom in a cab) | quick menu (hold Back) › Phone | gap |
  | folded: pick / run | ← → or the wheel / Enter, Space, click | D-pad, stick / A | gap |
  | open | Up | D-pad ↑ | gap |
  | back / put away | Esc (open: back, then fold), ↓ folded | B | gap |

  No pad button is free on foot (D-pad ↑ is the emote wheel), so the pad takes it out from the quick
  menu. While out it holds `UiFocus`: the body does not walk, items do not fire (`ItemController.UsablePlayer`
  is null, `HeldItemId` reads 0). VR target: the wrist menu puts the open phone in the left hand, poked
  with the ray (`XrUi`), travel behind `XrRig.Blink`; no camera moves in VR (#186).
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
