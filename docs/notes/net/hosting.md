# Hosting from the menu

- **What** (`Net/HostedServer`, `GameShell.Host`): Multiplayer -> Host... (name, port, LAN visible)
  starts this same executable headless as a dedicated server, waits until it answers the status
  query (`net/server-query`; up to 90 s, a full world takes ~30 s to listen), then joins
  `127.0.0.1:<port>` like any other server. Leaving the world, quitting or closing the window kills
  it (`OS.Kill`); leaving asks first, since it stops the game for everyone on it.
- **Command line**: `--headless --log-file user://logs/hosted-server.log [--path <project>] -- --server
  --port P --server-name N --parent-pid <client pid> --chunks <the client's chunk dir>`, plus
  `--generated-world` when there is no manifest and `--query-bind 127.0.0.1` when not LAN visible.
  `--path` only outside an export (`OS.HasFeature("template")`); the editor's own binary works too.
- **Orphans**: the server checks `--parent-pid` once a second and quits when the client is gone
  (crash, task manager).
- **Shared `user://`**: the hosted server and offline play use the same folder, so placed objects,
  interiors, loot and bank accounts of a hosted world sit next to the offline ones.
- **Errors**: if the process dies, the last useful log line is shown ("Port 7777 is already in use").
- **Check**: `--leavecheck host <port>` hosts, joins, leaves and asserts the process is gone.
