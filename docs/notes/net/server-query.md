# Server status query (LAN list, ping, players)

- **Protocol** (`Net/QueryResponder` server side, `Net/ServerQuery` client side): UDP on **game port
  + 1** (7778). Query = `USQ1` + uint32 nonce (8 bytes); reply = `USR1` + the nonce + UTF-8 JSON
  `{name, port, players, max, version, world, proto}` (`ServerStatus`). Ping is the reply time of
  that nonce, the median of the last three.
- **Uses**: the Multiplayer screen broadcasts every 2 s (each interface's directed broadcast,
  255.255.255.255 and 127.0.0.1, to the status ports of game ports 7777..7787) for "On your
  network", and unicasts each saved server every 4 s for its dot, players and ping. mDNS results
  (`net/lan-discovery`) are merged in; on the same endpoint the UDP answer wins. A reply from this
  machine is listed once, as `127.0.0.1:port`.
- **Server flags**: `--server-name <n>` (default: the machine name), `--query-port N`, `--no-query`,
  `--query-bind <ip>` (127.0.0.1 hides it from the LAN but still answers the client that hosts it).
  The responder replies from a byte snapshot rebuilt once a second on the main thread and
  rate-limits 10/s per source and 200/s overall, so it cannot amplify traffic at a third party.
- **Firewall**: open 7778/udp next to 7777/udp on a public box; on a Windows host the firewall asks
  on first run.
- **Probe**: `--discovercheck [seconds]` prints `[discover] udp <name> <endpoint> players= ping=` lines
  next to the mDNS ones.
