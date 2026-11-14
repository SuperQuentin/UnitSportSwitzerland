# Bots offline (single-player)

Bots were a dedicated server's only (`ServerWorld` made the `BotDirector`). Offline the game is its own server
(`OfflineMultiplayerPeer`, peer 1: every service authoritative, `Multiplayer.IsServer()` true), so the same system runs
there; #1330's option (a), for bowling partners on real bodies and the Moon colonists as real bots.

- **`ClientWorld.SetUpOfflineBots`** (offline launches, after the interiors): a `Players` container for the bots'
  bodies (the player stays the lone `Player` node outside it, as offline always), this world's own
  `PlayerReplication` spawner (`MultiplayerSpawner.Spawn` works on the offline peer) and
  `BotDirector.CreateOffline(spawner, players, local)`. `InteriorManager.Players` is that container, so the doors'
  quiet timer and the venues see the bots.
- **`TearDownOfflineBots`**: if a network game starts in this world (`StartNetworking`), the offline director, spawner
  and container go first; the server runs its own bots, and the network's own `Players` container takes the name.
- **The player is the human** (`BotDirector.Local`, `Humans()`): the director's "watched" test (a human within 300 m:
  a bot thinks at its own pace, else at most every 0.5 s) and `BotWorld.Bodies` (what a bot sees) include the offline
  player, who is not in the container. On foot only: in the free camera there is no body to watch or be seen.
- **Ground and collision**: nothing offline-only. A bot body registers itself as a collision anchor
  (`FootPlayer._EnterTree`) and the client's `ChunkManager` builds collision round every collision anchor, as for a
  remote player; the interiors' bot collision and walkable surface (`bots/interiors`) are built here.
- **`/bot`** offline: `ChatManager.HandleLocal` runs `BotDirector.Command` with you as the admin and your body as the
  sender's; `ChatCompleter` lists `/bot` offline.
- **Modes offline**: free roam (`/bot add`, `--bots`/`--botsat`, `WanderBrain`'s strolls and building visits) and
  `/bot arm|fire`. Battle Royale, Versteckis, Aura and Schwingen are server modes: offline only their client halves
  exist (`CreateClient`), so their bots stay on a server.
- **Check**: `tools/botindoorsolo.sh` (`--botindoorsolo`, `BotIndoorSolo`): one offline game on the generated world,
  one bot with `--botsindoors` beside a generated village; the player is put on foot (the game's own toggle), and the
  bot must be watched by it, go into a house, stand and walk inside, and come back out onto the street, on the path
  `botindoorcheck` checks on a server. `SHOTS=<dir>`: a window, the player 5 m behind the bot, frames before it steps
  through the door saved as PNG.
