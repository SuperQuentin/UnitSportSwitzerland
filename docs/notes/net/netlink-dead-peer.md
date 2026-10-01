# A dead ENet peer floods the log from every per-frame caller (`Net/NetLink`, #211)

- Once a client has lost its server (connection dropped, server killed, mid-teardown) the ENet peer
  is inactive, and **every** `Multiplayer.GetUniqueId()`, `IsServer()`, `IsMultiplayerAuthority()`
  or RPC logs `ERROR: The multiplayer instance isn't currently active.` with a C# stack. A caller in
  `_Process` writes that each frame: a user's log reached 235 MB.
- The worst offender was `ClientWorld.GetLocalNetPlayer` (`GetUniqueId` to find our body), behind
  every `LocalPlayer` lambda handed to managers (`RadioManager.Players`, `WebRadio.Players`,
  `RadioUi`, birds, ...). It now returns null while the link is down.
- `NetLink.Ready(node)`: offline peer, or a connected one, safe to ask. `NetLink.Online(node)`:
  connected to other machines. `NetLink.IsServer(node)`: `Ready && IsServer()`. Per-frame and event
  paths check these and skip network work; `WebRadio` (Serve, Want), `RadioManager` housekeeping and
  the radio panel's CD changer do.
- Check: loopback server + windowed client, kill the server process, wait 30 s after the client
  notices (`[occasions] disconnected`): `grep -c "isn't currently active"` in the client log is 0.
