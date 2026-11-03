# Phone interactions review

Lenses: keyboard-mouse, gamepad, vr, touch-mobile, information-architecture. Problems are ordered by severity within each lens.

## keyboard-mouse

### Problems
- **high** Up takes the phone out but is also the excavator boom and the first key of every folded/open nav. One key does three jobs; mashing Up while moving pulls the phone out and freezes the body (UiFocus). No letter-key take-out is documented. (PhoneController.cs:720-727, 757, 786-790)
- **high** Open phone has no keyboard way to fold or put away in one step; Esc is a multi-press ladder. Up/Down/W/S navigate the screen when open, so "S/Down puts away" only works folded. Esc walks Photo>Photos>Home>Fold, then again to put away (3-4 presses); Esc on Travel does nothing. Esc also doubles as pause. (PhoneController.cs:211-233, 778-806, 758)
- **high** Mouse and keys fight over the selection. `_keysLead` is cleared by every mouse event including motion, so a nudge hands the highlight back; no deadzone. (PhoneController.cs:955-956, 782-800)
- **medium** Folded: a click anywhere on the cover runs the selected quick action, not the one under the cursor. Wheel direction is opposite to swipe left. (PhoneController.cs:993, 962-963, 981-986)
- **medium** Answer/Decline come first only during a call; Open is last of 6. The list shifts by two when a call arrives, so an accidental Enter can Decline. (PhoneController.cs:557-572)
- **medium** Swipe thresholds are window pixels (14 px drag, >40 px sideways), not scaled; swipe-right Back conflicts with Map/photo panning and mouse drag-pan is swallowed. Back fires only on release. (PhoneController.cs:52, 982-990, 1005-1013)
- **medium** Clicks outside the screen are swallowed; right/middle buttons do nothing outside Camera; notes contradict on folded walking vs standing. (PhoneController.cs:740-745, 292-295)
- **low** Camera app overloads F/Tab/right-click for selfie and Space/E/W/S differently from the rest of the phone. (PhoneController.cs:1055-1062, 713)
- **low** Typing detection only checks LineEdit focus; Esc then leaves the page instead of the field. (PhoneController.cs:780-796)

### Strengths
- Every key is the phone's while out, so no accidental walking, firing or emotes.
- Simple consistent mouse model: press-release = click, drag past 14 px = swipe, wheel scrolls/picks.
- Finger follows cursor or keyboard focus with tap animation and sound.
- Pad and keyboard share one Direction/IsSelect abstraction.
- Take-out blocked while typing, with a toast naming the reason in a cab or busy state.
- Travel curtain cannot be cancelled by accident.

### Ideas
- Phone key as a toggle; Esc one step back only; small key legend via InputHints.
- Cover as a row of individually clickable tiles; reorder Open, Map, Camera, Contacts; pin Answer/Decline as a separate banner.
- 6 px mouse deadzone before `_keysLead` flips; distinct keyboard focus ring and hover ring.
- Scale swipe thresholds by window height; edge swipe or Back button for back; panning pages consume horizontal drags.
- Click outside to put away; right click = back; keys 1-6 jump to a quick action.
- One-time "Up to take out, Esc to put away" hint; unify the notes.

## gamepad

### Problems
- **high** Phone entry hidden behind hold-Back ring (0.3 s, aim right stick, release) with no on-foot discovery; tap Back opens inventory; D-pad up is the emote wheel. (phone.md; QuickMenu.cs:14,23,61)
- **high** Map search needs a text field; a pad cannot type. No on-screen keyboard, recents or categories; A pushes ui_accept into an empty field. (PhoneScreens.cs:438-442, 967)
- **medium** Camera: A jumps, shutter and Selfie buttons are FocusMode.None and unreachable; zoom steps not shown for LB/RB; A no longer shoots. (phone.md #1149; PhoneController.cs CameraInput; PhoneScreens.cs:873, 892, 895)
- **medium** Same button means different things per stage (D-pad up/down, B: put away / back / fold); exit takes B two or three times. (PhoneController.cs FoldedKey, Back() :211)
- **medium** Hints tiny and stage-specific; none for shoulder buttons or long lists. (PhoneController.cs:400-410; PhoneScreens.cs:1050-1053)
- **medium** Focus relies on Godot default neighbours; Home always focuses tile 0; Photos grid of 60 has no page jump; 1 s Activities rebuild can drop focus. (PhoneScreens.cs:962, 625, 979-982)
- **low** Cover: D-pad left/right only, no stick, wraparound without end-stop feedback. (PhoneController.cs Direction() :695-710)

### Strengths
- Every stage has a pad path and hints switch per device.
- The same focus drives the on-screen finger; visible 4 px focus border.
- UiFocus held while out, so the pad never walks or fires; camera app releases it deliberately (#1149).
- Page hints name accept/back per device; documented in the action maps.
- One input pipeline for mouse and pad.

### Ideas
- Direct pad chord on foot (hold D-pad up) plus a first-spawn toast; keep quick menu as fallback.
- Pad-first Map: recents, nearby, categories as rows; text field optional.
- Camera: A as shutter when standing still; 1x/2x/5x steps with LB/RB glyphs; focusable shutter/selfie.
- Symmetric B: hold 0.4 s puts away from any depth.
- Remember focus per page; LB/RB page up/down; keep focus by row id.
- One persistent glyph hint bar via InputHints.

## vr

### Problems
- **high** Travel needs the screen 14 cm from the eyes within 3 s; strains vergence, awkward pose, blink fires anyway. (XrPhone.cs:45, 373; PhoneController.cs:50, 639, 669-679)
- **high** Hinge by free-hand grip clashes with climbing/hotbar grips; edge not highlighted; hint says only "X open"; fold jittery near detents. (XrPhone.cs:43, 315-343; PhoneController.cs:403)
- **medium** X/B bound to physical buttons, no left-handed swap; Back on the pointing hand. (PhoneController.cs:805-822)
- **medium** Phone must be gripped constantly or it flies home; fatiguing, drops mid-interaction below 0.35. (XrPhone.cs:33, 51, 171-207)
- **medium** Touch at 6 mm / release 12 mm with weak depth cues; rearm 6 mm can double-tap. (XrPhone.cs:37-41, 241-247; PhoneScreens.cs:276-290)
- **medium** No hover state on buttons under the fingertip, no flick or inertia, swipes recognised only on lift. (PhoneController.cs:888-935)
- **medium** Ray invisible until it hits, free hand only; its trigger is the camera zoom step, so a stray pull zooms. (XrPhone.cs:49, 261-307)
- **low** On-screen hint may not be visible in headset and omits most VR gestures. (PhoneController.cs:399-409; XrWristMenu.cs:102-103)
- **low** Fingertip ring is white, not the amber accent. (PhoneScreens.cs:276-283)
- **low** Ear-answer geometry approximate (head*(+-0.075,-0.02,0.01), 10 cm radius); can answer by accident near the eyes. (XrPhone.cs:45, 376-385)
- **low** Selfie auto-trigger (0.3 s, cos>0.5) can fire unwanted. (XrPhone.cs:47, 350-367)

### Strengths
- Camera never moves (#186); body walks; every gesture has a button way (R5 of xr/vr-action-map).
- Pocket zone in head yaw frame, mirrored for left-handed play; wrist menu covers unreachable cases.
- Hysteresis everywhere avoids flicker.
- Phone let go away from the pocket hangs 0.5 s then flies home.
- Haptics for touch, detents and calls.
- Dedicated VR layout and a scripted `--phonevrcheck --xrsim` probe.

### Ideas
- Travel look at 25-45 cm facing the eyes, blink after about 1.5 s.
- Edge ghost within 15 cm plus one-time "grab the edge to open"; detent magnetism.
- Button hover state; amber ring shrinking with depth.
- Flick with inertia; live swipe preview on the cover.
- Faint ray whenever the free hand points near the phone; pinch tap for hand tracking.
- Swap X/B by handedness; add the swing gesture to the in-VR hint.
- Latch or toggle option to reduce grip fatigue.
- Selfie needs a clear turn or an on-screen button; verify ear-answer on a headset.

## touch-mobile

### Problems
- **high** No touch-native entry; only QuickMenu "Phone"; no touch column in the controls table. (QuickMenu.cs:61-62; phone.md; TouchLayout.cs)
- **high** All touch is the mouse path; SwipePixels=14 px (about 1 mm) and 40 px back swipe are not mm-based, so taps become swipes. (PhoneController.cs:51-52, 983, 1005, 1009; touch-controls.md:17)
- **high** Tap targets on the inner screen are about 2.5-3 mm (rows 54-58 units on an 800-unit canvas), far under 7-9 mm; 24 pt text about 1.2 mm. (PhoneScreens.cs:199, 417, 838, 891, 1056-1064; touch-controls.md:19)
- **medium** Touch cannot back out, fold or selfie; no zoom steps; cover gives no touch hint. (PhoneController.cs:1056-1065, 983-987; PhoneScreens.cs:873-891)
- **medium** Camera: one touch shoots and looks at once. (PhoneController.cs:1056-1065)
- **medium** Map search keyboard covers the lower half of the landscape screen. (PhoneScreens.cs:438-439; PhoneController.cs:720)
- **low** Thumb reach and hover animation assume a cursor touch never has. (PhoneController.cs:46, 439)

### Strengths
- Touch becomes mouse input, so drag-scroll and sideways swipes already work.
- mm-based TouchLayout with DPI/safe-area handling and TouchLayoutTests; 7.5 mm hotbar precedent.
- #1149 walking while the camera app is open is a good base.
- Quick menu lists Phone with an "On foot only" reason; swipe-to-pick cover suits thumbs.

### Ideas
- Phone button / More-sheet row in the on-foot layout with notes rows.
- Swipe thresholds in mm via pxPerMm (3 mm, 10 mm), 2 mm slop.
- Scale the inner screen on touch (square-on PhoneCover); 7 mm min target, 9 mm primary.
- Touch chrome: Back chevron, fold handle, big shutter/selfie/1x 2x 5x; look as drag on the free area.
- Use the virtual keyboard height to lift the Map search.
- Big tap rows on the cover, press highlight instead of hover.
- Touch case in `--phonecheck` at 2220x1080 asserting mm sizes.

## information-architecture

### Problems
- **high** Back from Training folds the phone because Page.Training is not in Back()'s list; same hazard for any new Page. (PhoneController.cs:224-232; PhoneScreens.cs:21)
- **high** Contacts row has four same-weight buttons per player, no confirm, no feedback; "Call" is a misnomer; four focus stops per person. (PhoneScreens.cs:~519-532, 492)
- **high** Home has "Activities" and "Menu" opening overlapping things; Menu and Journal close the phone into full-screen overlays; descriptions only in tooltips. (PhoneScreens.cs:401-408)
- **medium** Cover quick actions and Home tiles disagree; Open is last. (PhoneController.cs:557-575)
- **medium** Home grid has 6 columns for 8 tiles while VR is 3x2. (PhoneScreens.cs:396; phone.md VR)
- **medium** Hint lines are fixed per page and sometimes wrong (Map "travel there" on the search field; Activities "join" on empty). (PhoneScreens.cs:450, 564, 790, 895, 1050-1052)
- **medium** Empty and error states are text-only dead ends; Travel failure only toasts; curtain has no cancel. (PhoneScreens.cs:460, 467, 600, 623, 782; PhoneController.cs:231, 570-576)
- **medium** Map is search-only: no recents, nearby, distance or fog/places (#1224). (PhoneScreens.cs:436-476)
- **low** Photos: only Set as lock/home; no next/previous; two Backs to leave. (PhoneController.cs:213-218; PhoneScreens.cs:782-840)
- **low** Camera is modal and differs from the rest; three Backs from selfie. (PhoneController.cs:219-223, 854)

### Strengths
- Consistent Back model and a two-key hint line that switches per device.
- Incoming call first on the cover.
- Travel curtain waits for cover, shows destination, explains denial.
- One screen layout shared by all devices.
- Cover-to-app shortcuts save a Home step.
- Per-device notes table and a `--phonecheck` probe.

### Ideas
- Generic Back (any page except Home/Travel goes up), visible Back chevron, Cancel on Travel.
- Home cut to 6 apps (3x2): fold Menu and Training into Activities; labels and badges under tiles.
- Contacts: one row per player, detail sheet with Go to / Bring here / Party / Fight, confirm on Fight and Bring here, result line; rename away from "call".
- Map: Recents, Near you, distance, category chips; picker instead of keyboard-only on pad/VR.
- Empty states with action buttons; persistent error banner.
- Cover with status line and reorder by recent use.
- Hints derived from the focused control via InputHints.
- Screens to design first: Home 3x2 + badges, cover with status, Contacts list + sheet + confirm, Map with recents, Travel with Cancel, empty states, camera/photos flow, call/invite screen.
