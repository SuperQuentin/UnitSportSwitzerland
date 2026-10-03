# Key hints

- **Key hints** (`Core/InputHints`, issue #32): never type a key into a UI string. `Label(action)`
  names the binding for the device in hand — keyboard keys through
  `DisplayServer.KeyboardGetLabelFromPhysical`, so the physical-Z engine key prints **Y** on a Swiss
  QWERTZ keyboard and **W** on AZERTY; Xbox names on a pad. `Format("{use_item} to eat")` fills
  `{action}` placeholders, which is how item, mount and car blurbs name their controls.
  `PromptBar` (bottom right) asks `ClientWorld.Prompts` a few times a second what applies *now*
  ("Get in the helicopter [E]", "Travel [R]", fly-camera keys), resolving the player from the camera
  so probes get it too. `ControlsHelp` (**F1**, or Controls on the title and pause menus) lists every action with a keyboard
  and a pad column from the live `InputMap`; it reads input in `_Input` because it opens over the
  menus, which would otherwise take its Esc. `--controls` opens it for a screenshot.
- **Three devices (#435).** `PlayerInput.HintDevice` is what prompts name: keyboard, pad, or **VR**
  while the headset is on (`LastDevice` stays Gamepad there, for pad-only behaviour such as focus
  and the sprint latch). VR names come from `XR/XrPad.Control` (the controller input that replays as
  the action's pad event, which depends on the triggers' role: shoulders on foot, triggers mounted)
  and `XR/XrControlNames` (per controller family: Quest by default, Index, Vive, WMR, picked by
  `XR/XrProfile` from the OpenXR interaction profile; `--xrprofile index|vive|wmr` forces one).
  An action with no VR control falls back to its keyboard key. The label cache is keyed by device
  and that trigger role; `PlayerInput.HintsChanged()` re-raises `DeviceChanged` when either changes.
  - Screens that read a pad button or a key directly name it with `InputHints.Button(JoyButton)` /
    `InputHints.Keyboard(Key)`; `InputHints.Pad` / `Vr` pick between a pad and a mouse wording.
  - F1's second column is **VR** (green) in the headset; its vehicle groups are named as mounted
    (`XrPad.AssumeShoulders`). Full per-action VR design: `xr/vr-action-map`.
  - Still typed by hand, on purpose: keyboard-only tools (chat, F3/F4 perf, F11, GPX replay, video
    export, the wheel binding panel, `/spawn` Tab).
