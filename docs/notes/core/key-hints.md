# Key hints

- **Key hints** (`Core/InputHints`, issue #32): never type a key into a UI string. `Label(action)`
  names the binding for the device in hand — keyboard keys through
  `DisplayServer.KeyboardGetLabelFromPhysical`, so the physical-Z engine key prints **Y** on a Swiss
  QWERTZ keyboard and **W** on AZERTY; Xbox names on a pad. `Format("{use_item} to eat")` fills
  `{action}` placeholders, which is how item, mount and car blurbs name their controls.
  `PromptBar` (bottom right) asks `ClientWorld.Prompts` a few times a second what applies *now*
  ("Get in the helicopter [E]", "Travel [R]", fly-camera keys), resolving the player from the camera
  so probes get it too. `ControlsHelp` (**F1**, main menu Controls) lists every action with a keyboard
  and a pad column from the live `InputMap`; it reads input in `_Input` because it opens over the
  main menu, which would otherwise take its Esc. `--controls` opens it for a screenshot.
