# Name tags over other players' heads (#1136)

- **What**: every other player this client has (vision interest already despawns those out of sight)
  wears a `Player/NameTag` (`Label3D`, billboard, depth-tested so walls hide it, drawn to 60 m like
  `Bots/BotTag`): their name, and `VR` or `MOBILE` on a second line on a headset or a phone/tablet.
  Not on one's own body, not on race NPCs or movie puppets; bots keep their `BotTag`, sleepers their
  `Net/Sleepers` tag. `TopLevel`, placed each frame at `FootPlayer.HeadTop` + 0.2 m, so a rider's or
  a passenger's tag sits over where they are drawn.
- **Name**: the server's, never the client's claim: `ChatManager.SendTagNames` sends the
  peer -> name map of `InWorld` players to everyone (`ReceiveTagNames`, `TagNames`,
  `TagNamesReceived`) after a join's name, `/name`, and a leave. Text is set only on change.
- **Device**: `FootPlayer.DeviceKind` (`DeviceDesktop` 0, `DeviceVr` 1, `DeviceMobile` 2), set by the
  owner every frame from `XrSession.Active` / `Platform.IsMobile` (a headset can start mid-game),
  replicated on change. Protocol 46.
- **Input**: none, nothing to press; bots: nothing to do (no action, a bot reads no tags).
- **Check**: `tools/nametagnetcheck.sh` (A `--mobile`, B sees "TaggerA / MOBILE" over A's head, none
  on itself, and A's `/name` follow). `SHOTS=1` saves B's view of A to `test_output/nametagnet_B_tag.png`.
