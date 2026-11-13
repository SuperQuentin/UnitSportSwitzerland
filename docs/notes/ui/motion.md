# One motion language: `UiMotion` (#1831)

`Ui/UiMotion` is a static helper that animates panels and controls: short, springy, tweens only. The numbers come
from the mock-up keyframes (`docs/plans/menus-hub-rework/M06.html`, `M07.html`).

| Call | What | Time |
|---|---|---|
| `Open(panel)` | fade in, scale .94 to 1.022 to 1 about the centre | 0.18 s |
| `Close(panel, then)` | scale to .97 and fade out, then `then` once (free or hide there) | 0.10 s |
| `Press(button)` | squash to .94, spring back | 0.08 + 0.12 s |
| `Pop(c, from = 1.4)` | scale `from` to 1 with the spring: a changed count, a landed item | 0.24 s |
| `SlideTo(highlight, rect)` | a selection moves and resizes to a row with the spring | 0.12 s |
| `Stagger(rows, step = 0.02)` | an `Open` per row, `step` apart | 0.18 s each |

The spring is `Tween.TransitionType.Back` ease out (`SpringTrans`, `SpringEase`); the durations are `const`s.

## Rules

- **Never delays input.** The caller drops input at once (pops the page, stops the pointer, releases focus) and
  only the picture lingers. `Screen`s: the shell pops a `Springy` page from its stack on Esc, makes its controls
  ignore the mouse, keeps the menu layer up for the 0.10 s, then frees it (`GameShell.Retire`). Keys work from the
  first frame; the open never waits.
- **Reduce motion and VR.** `GameSettings.ReduceMotion` (Settings › Video › Menus, the same flag the menu skins
  read) or `XrSession.Active`: every call becomes a plain 0.1 s fade; Press, Pop and SlideTo are instant;
  `Open` leaves the scale at exactly 1.
- **Re-entrant.** The running tween lives in the control's metadata; a new Open, Close, Press or Pop on the same
  control kills it. A control never stays half-scaled or half-transparent: Open ends at scale 1, alpha 1.
  A Close cut short by an Open (or another Close) runs its `then` at that moment, so it still runs exactly once;
  a caller that frees the panel in `then` must not Open the same panel again. Press and Pop do nothing while the
  control opens or closes.
- **No per-frame cost.** Tweens are created on the event; there is no `_Process`. Timing ignores the game's time
  scale and runs on frames, so `--fixed-fps` and headless work; a tween needs the control in the tree (outside it
  the call jumps to the end state).

## Where to use which

- A new page or popup: make a `Screen` `Springy` (override `Springy => true`), or call `Open`/`Close` on the root of
  a HUD panel. Do not scale a page whose children are `SubViewport`s or a map that is still loading.
- A button's own feedback: `ButtonDown += () => UiMotion.Press(b)` (the pause menu's entries).
- A number or an item that just changed: `Pop`. A selection bar that follows the focus: `SlideTo`. Rows of a list
  appearing: `Stagger`.

Applied so far: the pause menu (open, close, press). The other pages keep the shell's fade until their slice of
#1831 takes the helper. Check: `--motioncheck --systems ui` (`Ui/MotionCheck`), and `--menucheck` for the pause menu.
