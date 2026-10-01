# LAN discovery (mDNS)

- **What:** the Multiplayer screen lists the dedicated servers on the LAN under "On your network"; a click
  joins. On by default, Settings > Gameplay > "Find servers on your network" (`GameSettings.LanDiscovery`). Browses
  only while that screen is open. Two sources are merged there: this mDNS browse (servers deployed on Linux with
  avahi) and the game's own UDP status query, which every server answers and which carries players and ping
  (`net/server-query`). On the same endpoint the UDP answer wins.
- **Protocol** (`src/Net/LanDiscovery.cs`, BCL only): every 3 s a PTR query for `_unitsport._udp.local` to `224.0.0.251:5353`,
  from an **ephemeral port** = legacy unicast query (RFC 6762 §6.7): the responder answers straight to that port, so no
  multicast join and no clash with the mDNS responder Windows already runs on 5353. One socket per up IPv4 interface
  (a multicast send leaves on one interface only; WSL/VPN/Tailscale adapters would otherwise swallow it). Entries expire 15 s
  after the last answer; a TTL-0 PTR (goodbye) removes one at once.
- **Answer handling:** PTR -> instance, SRV -> port, A -> address, TXT `version=`. A PTR without SRV triggers a direct SRV+TXT
  query; with no A record the responder's own source address is used (the box that answers is the server).
- **Server side:** avahi advertises it, installed by `tools/deploy-linux.sh` (`docs/notes/general/linux-deploy.md`); ufw must
  allow 5353/udp. Windows Firewall lets the unicast reply in through its default "unicast response to multicast" rule (3 s window).
- **Probe:** `<godot> --headless --path . -- --discovercheck [seconds]` lists what answers (mDNS and UDP lines) and prints
  `[discover] RESULT: ok`, or `FAILED` when nothing did. A plain Python responder on 5353 (SO_REUSEADDR + group join) is
  enough to test the mDNS side without a Linux box.
