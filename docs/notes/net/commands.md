# Commands

- Dedicated server with operators:
  `<godot> --headless --path . -- --server --admin-password <pw>`; type commands straight into
  its stdin (`/admin add <name>`, `/say ...`, `/tpall <town>`). Client: add `--name <n>`.
- Server binds the IPv6 wildcard (`::`, dual-stack) so it answers on every interface including
  Tailscale; `--bind <ip>` restricts it to one. **ENet is UDP** — a forwarded port must be a
  UDP rule and TCP-only tunnels (ngrok free, Cloudflare Tunnel) cannot carry it.
  `--stream-bandwidth <MB/s>` caps terrain streaming per client; the 3 MB/s default is
  24 Mbit/s each and is a LAN figure. `--tiles-url <url>` (or `UNITSPORT_TILES_URL`) names a static
  HTTP mirror of the chunk directory that clients take tiles from first (`net/http-tiles`).
- Status query (LAN lists, players, ping; `net/server-query`): `--server-name <n>` (default: the
  machine name), `--query-port N` (default port + 1), `--query-bind <ip>`, `--no-query`.
  `--parent-pid <pid>`: quit when that process is gone (servers hosted from the menu, `net/hosting`).
- `--player-status`: one `[server] player <id> at <pos>, ground ...` line per player every 5 s
  (off by default: each `GD.Print` costs ms on Windows, see `net/load-testing`).
- Deploying the server to a Linux host (tmux console, cron, firewall, mDNS): `general/linux-deploy`.
