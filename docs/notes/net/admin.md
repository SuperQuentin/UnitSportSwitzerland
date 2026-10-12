# Admin

- **Admin** (`Net/PlayerRegistry`): identity is the ENet peer id, which a client cannot forge;
  the display name is a *request* that the server sanitises and deduplicates. Operators come
  from `user://admins.json` (granted on join) or `/login <pw>` against `--admin-password`.
  Without that argument `/login` is disabled entirely. Hosting from the menu (`net/hosting`)
  makes the host an admin: `HostedServer` passes a random `--host-token`, the client sends it back
  after its name (`ChatManager.ClaimHost`), `PlayerRegistry.TryClaimHost` elevates that peer for
  the session. `Net/ServerConsole` reads the dedicated
  server's own stdin on a background thread (`Console.ReadLine` blocks, so it cannot be on the
  main loop) and runs commands as peer id 0, which is always an operator — that is how the
  first admin gets granted on a fresh server. `PlayerInfo.AdminChanged` (any grant or loss: join,
  `/login`, `/admin`) makes the server's `ChatManager` send that client `AdminStatus`, which sets
  `Core/Permissions` so its menus can follow; `IsAdminPeer` exposes the check to other server
  systems. `NameAssigned` fires when a peer's name is set, for the bank, whose accounts are keyed
  by it. `/update` (admin): installs the newest release on a deployed server (`net/server-update`).
