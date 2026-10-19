# The phone in VR (#920)

Status: **planned**. Follows the desktop phone (#889, `ui/phone`), which VR refuses today (a gap in
`xr/vr-action-map`).

| Phase | State | What |
|---|---|---|
| 1 In the hand, the finger on the glass | planned | `XR/XrPhone`: out of a pocket with a grip, held in the off hand; the real index taps, drags and hovers; haptics; the cover's quick actions |
| 2 Two hands and the arm | planned | Unfold by swinging the free half open; the camera app aimed by the arm; a selfie by turning the phone round |
| 3 The body as the curtain | planned | Travel by raising the phone to the eyes (a blink otherwise); a call answered at the ear; the phone home to its pocket on release; a VR-sized layout |

## Why it must be its own design

The desktop phone is built around the game's camera: over the shoulder, onto the screen, a black
backdrop that hides teleports. In VR none of that may happen: the eye only moves with the head
(#186), and a moving or fading camera is the quickest way to make a player sick. What VR offers
instead is better: you *have* the phone, in your hand, at the distance you choose, and you have a
finger to touch it with. So the phone becomes a physical object first, and every desktop shortcut
becomes something the body does.

The rules of `xr/vr-action-map` decide most of it:
- **R1, hands for physical things**: the phone is a thing you hold, so a grip takes it, a fingertip
  presses it, the other hand opens it.
- **R4, the body before a button**: aiming the camera is aiming the arm; a selfie is turning the
  wrist; travel is putting the screen before your eyes.
- **R5, nothing unreachable**: every body gesture keeps a button way (the wrist menu, a ray and a
  trigger), so the phone also works seated and without hand tracking.

## 1. In the hand, the finger on the glass

**Where it lives.** In the left front pocket: a grip closing about 0.7 m below the eyes, 0.15 m to the
left and a little forward (the hip hotbar's zones are at the sides and stay as they are) takes the
phone out into that hand, folded. Left-handed play (#439) mirrors it to the right pocket. The wrist
menu gets **Phone** for those who cannot reach (R5).

**How it is held.** `XR/XrPhone` puts the existing `PhoneModel` on the holding hand's grip pose
(`XrRig` `LeftGrip`), as a real phone sits in a palm. The cover faces the palm's side, so turning
the palm toward the face shows the screen. The real hand (`XrHand`) closes round it: its fingers
take a hold pose instead of the controller's grip.

**The finger.** The free hand's index tip is the pointer:
- **With hand tracking**, it is the tracked `IndexFingerTip` joint (`XRHandTracker`, already read by
  `XrHand`).
- **With controllers**, it is the end of the drawn index bone while the index is lifted off the
  trigger. The fingers are inferred from the trigger's touch sensor (`real-hands`), so pointing at
  the phone is natural: you lift your finger.
- **Hover**: within 3 cm of the glass, a soft ring shows on the screen under the tip. That is the
  cursor: you see what you are about to press, which matters because there is no glass to feel.
- **Touch**: the tip crossing the glass plane (within 6 mm, released past 12 mm: the radio's
  arm and re-arm, `XrHands.PokeRadio`) is a mouse press at that point of the screen. Moving while
  touching is a drag: a list follows the finger and a flick keeps scrolling. Lifting off is the
  release, so a tap is a click. The screens take it through the same `PushInput` as the desktop
  mouse: no new UI.
- **Haptics**: a 10 ms tick on the poking hand at touch and at release (glass has an edge you can
  feel), and a lighter one on the holding hand. Rumble goes through `XrSession`.
- **The ray as the fallback**: the free hand's aim ray with the trigger, as `XrUi` already does for
  the panel, when the finger is not near the phone.

**What changes from the desktop.**
- The game's camera does not move: `PhoneLook`, `PhoneCover` and the backdrop stay at 0 in VR.
- The body does not stop when the phone comes out. In VR you walk with the stick while you read,
  as with a real phone.
- `FootPlayer.VrHands` already carries the hands to the figure, so others see the phone in the
  avatar's hand. `PhoneState` stays the one replicated byte.

**The cover's quick actions**: a sideways swipe of the finger picks, a tap runs, as on the desktop
cover.

## 2. Two hands and the arm

**Unfolding.** The free hand grips the phone's loose edge (a grip within 4 cm of the front half's
free side) and swings it open about the hinge: `PhoneFold` follows the hand's angle, with a detent
click at flat and at shut. Letting go between the two settles to the nearer one. The button way is
X (A for the left-handed) on the holding hand: tap to open, tap to fold.

**The camera app.** The phone *is* the camera. Its back lens (`PhoneModel.LensPoint`) films where
your arm points it; the inner screen shows that view, as the desktop app already renders it.
- **Shutter**: the holding hand's trigger, as a phone's volume key is a shutter; or a poke on the
  on-screen button.
- **Zoom**: a finger sliding up and down the zoom strip, or the holding hand's stick.
- **No forced level**: unlike the desktop, there is no "level with the horizon" in VR: your hand is
  the level, and tilting the phone tilts the photo as in life.

**A selfie by turning the phone.** No key: when the phone's back (the lens and the cover) faces
the head (the lens-to-eye dot product past 0.5, held for 0.3 s), it is a selfie. The cover lights
up as the viewfinder, exactly the foldable's rear-camera selfie of the desktop pass. The avatar
does its selfie faces (Happy, Joy, Wink, Love on the shared clock). The face looks into the lens
because the lens is where your real hand put it. Turning it back ends the selfie.

## 3. The body as the curtain

**Travel.** On the desktop, the screen covers the view and the world changes behind it. In VR the
player can do that literally: after picking a place in Map, the screen says "Bring the phone to
your eyes". With the phone within 12 cm of the eyes, covering most of the view, the teleport runs
behind it. The phone is the curtain, made by the player's own hand, so there is no fade to get sick
from. If the phone is not raised within 3 s, `XrRig.Blink()` does it (a short black blink, the
existing VR teleport). `Teleporter.Curtain` gets a VR answer: it holds the jump until either.

**Calls.** An incoming invite buzzes the hand nearest the pocket in the invite pattern (two
pulses), and the ringtone plays from the pocket's position, spatialised. Raising the phone to an
ear (the cover within 10 cm of either ear) answers, as on a real phone; putting it back unanswered
lets it ring out. The toast's buttons and the cover's Answer stay as the other ways.

**Putting it away.** Back to the pocket zone and let go. Let go anywhere else and it flies home
to the pocket after half a second, with a whoosh: a phone dropped in a lake is not a fun mechanic.

**A VR layout.** At arm's length the 16 cm screen is about 23° wide, about 600 headset pixels.
The screens get a VR scale: targets at least 18 mm (about 1.6× the desktop size), fewer tiles per
row (3 × 2), and the status bar text larger. `PhoneScreens.Fit` picks the scale by mode.

## Devices table (the rows for `xr/vr-action-map`)

| Action | Keyboard / Pad | VR |
|---|---|---|
| Take out | Up / quick menu › Phone | grip at the left front pocket; wrist menu › Phone |
| Pick a quick action | A D / D-pad | the finger swiped across the cover |
| Open / fold | W, S / D-pad ↑ ↓ | the free hand swings the half open or shut; X on the holding hand |
| Press | Enter, click / A | the index tip on the glass; the ray + trigger |
| Scroll | drag, wheel / stick | the finger dragged, flicked |
| Back | Esc / B | a swipe right on the screen; B |
| Shoot | click, Enter / RT | the holding hand's trigger; a poke on the shutter |
| Selfie | F / Y | turn the phone round |
| Travel (curtain) | automatic (the lens closes in) | raise the phone to the eyes; a blink after 3 s |
| Answer a call | cover › Answer | raise the phone to the ear; a poke on Answer |
| Put away | S, Esc / B | back to the pocket; let go anywhere, it returns |

## Checks

- `--xrsim` with scripted hands: a `--phonecheck vr` mode drives the sim hands. It takes the phone
  from the pocket, pokes a tile (a press and a release at its point), drags a list, swings the phone
  open, turns it for a selfie, and raises it to the eyes for a travel. It checks the same results as
  the desktop check.
- Finger tracking itself needs a headset: Quest hand tracking over Link, judged in a playtest
  scenario.

## Bots

Nothing new: the phone stays presentation over actions with server paths.

## Open questions

- Which hand holds the phone by default: the off hand (left), so the dominant hand points.
  Left-handed play flips it.
- A virtual keyboard for the Map search, or only the popular places plus voice later? Phase 1:
  the popular places and the nearest towns, no typing; a poke keyboard if playtests ask for it.
