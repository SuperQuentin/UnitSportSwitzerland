# Never capture "the thing the player controls" at startup

- **Never capture "the thing the player controls" at startup.** The teleport search held the
  spectator camera from `_Ready`, so in multiplayer — where you are an on-foot networked
  `FootPlayer` — Tab silently moved a camera that was not even current and nothing appeared to
  happen. `Teleporter.ActiveTarget` is a delegate resolved per jump.
