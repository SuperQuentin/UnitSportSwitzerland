# MVR-xchange: rigs live with lighting tools on the LAN (#960)

- **What.** MVR 1.6's network exchange: stations of a *group* tell each other about MVR revisions
  (COMMIT) and fetch them (REQUEST). The game is a station on this machine (client side, like
  `/concert import`): it pulls a rig from grandMA3, Vectorworks, Capture, BlenderDMX... into the hosted
  set, and offers ours. Code: `src/Concert/Mvr/MvrXchange.cs` (framing, messages), `MvrXchangeStation.cs`
  (sockets), `Mdns.cs` (discovery), all pure .NET; `ConcertMvrTool` (commands, main thread).
- **Commands.** `/concert xchange on [group] [local]` (default group `Default`; `local`: loopback only,
  no mDNS), `off` (LEAVE, closes everything), `list`, `pull <n|uuid>` (saved to `user://concert/mvr/`,
  then imported as `/concert import` does: the operator's hosted set only), `commit [comment]` (exports
  the nearest rig as `/concert export` does and offers it), `join <ip:port>` (a station mDNS cannot
  see), `ws <ws://url>` (WebSocket mode). The desk's MVR row: *Xchange on/off*, *Commit rig*, the
  offered files, *Pull*, and a status line. Off by default: nothing listens before `on`. UI buttons and
  chat only: no new key, so no VR/pad action row; bots need nothing (an operator's LAN tool).
- **TCP mode.** Package = 28-byte big-endian header (778682, version 1, number, count, type 0 JSON /
  1 file, uint64 length) + payload. We send every message, files too, as one package; we read several
  (joined in arrival order: libMVRgdtf numbers its one file package 1, not 0). Caps: JSON 1 MB, a file
  64 MB, 65536 packages, 16 connections; a bad header drops the connection. Outgoing: a connection per
  message (connect 4 s, answer 10 s, file 2 min), as libMVRgdtf and BlenderDMX do; incoming: answered
  in turn until the peer closes or is quiet 60 s.
- **Messages.** JOIN / JOIN_RET carry `Provider` "UnitSport", `StationName` (the player's name),
  a `StationUUID` kept in `user://concert/xchange_station_uuid`, `verMajor` 1 / `verMinor` 6 and the
  `Commits`. REQUEST's `FromStationUUID` is sent as an array (the spec's table; its example shows a
  string): read both. Read leniently: numbers and bools as strings, LEAVE's `StationUUID`, an older
  `Files` list, UUIDs case-insensitive. NEW_SESSION_HOST is answered OK false (not followed).
- **Who reaches whom.** A JOIN tells us a station but not where it listens: we can only COMMIT to
  stations heard on mDNS or joined by address. Discovery joins every station of our group it hears
  (again after 30 s when one did not answer); a station that only joined us gets our files in its
  next JOIN_RET.
- **mDNS.** UDP 5353 shared with Windows' own responder (address reuse), 224.0.0.251 on every IPv4
  interface; query at once, 1 s, 3 s, then every 30 s. Ours: instance
  `UnitSport-<uuid8>.<group>._mvrxchange._tcp.local`, PTRs from `_mvrxchange._tcp.local` and from
  `<group>._mvrxchange._tcp.local`, SRV, TXT `StationName`/`StationUUID`, an A per interface (as
  Perastage, said to work with grandMA3). libMVRgdtf and BlenderDMX name theirs `<group>._mvrxchange…`
  alone, so all of a group share one name and python-zeroconf keeps only one of them; a reader takes
  the label before the service as the group, which reads both forms.
- **WebSocket mode.** `ClientWebSocket` to the server's URL: JOIN as a text frame, COMMIT and REQUEST
  both ways as text, files as binary frames (one request at a time: a binary frame does not say what
  it answers).
- **Threads.** Sockets on tasks; news (lines, a pulled file's path) in a `ConcurrentQueue` drained by
  `ConcertMvrTool.Pump` from `ConcertService._Process` (nothing allocated while it is empty); late
  lines go to the chat that ran the last xchange command.
- **Checks.** `MvrXchangeTests` (framing round trips, malformed packages and DNS, message fields, two
  stations on loopback: join, commit, pull 3 MB byte-identical, an unknown file, garbage on the
  socket; WebSocket mode against an `HttpListener` server); `tools/concertnetcheck.sh` (A's station
  pulls from a station of the probe's, B gets the 124 fixtures; A commits ours, the desk pulls it
  byte for byte). Interop (Oct 2026, not committed: GPL code): BlenderDMX's own `mvrxchange` TCP
  client and server (bpy stubbed) both ways — JOIN, COMMIT, REQUEST, LEAVE, files byte-identical;
  python-zeroconf (BlenderDMX's discovery) resolves our announcement and we resolve and join its.
- **Not done.** No grandMA3 / Vectorworks / Capture tried (none here); MVR_NEW_SESSION_HOST not
  followed; IPv6 (AAAA) neither announced nor read; no WebSocket *server* of our own; a pulled file is
  imported at once (onto the hosted set) rather than offered as a merge.
