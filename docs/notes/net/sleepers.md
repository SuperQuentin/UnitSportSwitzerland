# Sleepers: who left lies asleep where they were (#644)

- `Net/Sleepers` at `World/Sleepers` (server and every client). A player who disconnects stays as a body
  lying on its back (the knocked-out angle, -1.45 rad about X), in its outfit, figure and hat, with a
  billboard `Name (asleep)` tag (drawn to 60 m). Look only: nothing can be done to it. On reconnecting,
  that player wakes there (`WakeAt` -> `Teleporter.TeleportTo`, the `/tp` path: it settles on the
  **terrain**, so a sleeper on a bridge or a deck wakes on the ground under it).
- **Identity is a key, not the name** (names are a request anyone can make): `Net/PlayerKey`, ECDSA
  P-256 made once in `user://identity.key` (PKCS#8). On accept the server sends `Challenge(nonce)`; the
  client answers `Prove(publicKey, signature)` over `"unitsport-identity-v1:" + nonce`; a good one makes
  `PlayerKey.Fingerprint` (128 bits of SHA-256, hex) that peer's identity for the session. One nonce per
  join (no replays). A peer that never proves (old client, bot) leaves no sleeper; a key already in use
  by a connected peer (two clients on one `user://`) gets no identity.
- **Server**: `SleeperBook` (pure, unit tested in `SleeperTests`) keeps one sleeper per key, saved to
  `user://sleepers/server.json` through `JsonStore.SaveAsync`, so they survive a restart. The key never
  goes to clients; they know a sleeper by its `Id`. `ServerWorld.OnPeerDisconnected` calls `PeerLeft`
  **first**, before `ReportDisconnect` forgets the name and before the body is freed. Riding or indoors
  (interiors are ~3000 m below), the sleeper goes on the ground under `NetGlobal` (`GroundAt`); no
  ground loaded there = no sleeper.
- The figure comes from `Rider` = the peer id it had (jersey hue, and the figure when the player never
  chose one), `AppearanceBits`, `OutfitBits`, `HeadwearId`.
- Check: `tools/sleepercheck.sh` (net tier): A steps 30 m and leaves, B (own `user://`) sees one
  sleeper, A comes back with the same `user://` and must wake within 3 m of it while B sees it go.
