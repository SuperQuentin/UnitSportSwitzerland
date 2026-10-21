# Joining for the chat only, whatever the version (#1088)

- **Why:** the wire protocol moves several times a day and `Net/Handshake` refuses a client of another one before
  it can type anything, so an admin with a newer game could not reach an older server to `/update` it (the live
  server sat on v0.79.0 behind v0.84.0, only SSH got in).
- **What it is:** as if fully joined, minus the body. The player keeps their name and the rights that name has on
  that server: admin when it is in `admins.json`, or after `/login`, exactly as in game. **No password of its own**
  (a first version had one in front of the server's operator console; the user wanted the join's model instead).
  Admin by name is as weak as it is for a join, no weaker.
- **Server** (`Net/ConsoleLink` at `World/ConsoleLink`): `Command(name, line)` from any peer, never through the
  handshake. The first one joins the peer as a `PlayerInfo.Bodiless` player (`ChatManager.SubmitFromConsole`:
  `PlayerRegistry.Add(peer, bodiless: true)`, `Rename`, "X joined the server console"), `Handshake.Exempt`s it
  (no hello timeout kick) and greets it with the server's name, version, protocol and who it is there. Every line
  then goes where a game's `SubmitLine` goes: `/…` a command as that player, anything else said to everyone.
- **Its lines go through the link, never the chat's RPCs** (its game may not share them): `ChatManager.RemoteLine`
  for `ReplyTo`, every `Broadcast`, the admin notice (`OnAdminChanged`), and a kick (`KickPeer`: the reason, then
  the disconnect). `ConsoleLink.Reply` sends them.
- **No body:** `RequiresAvatar` refuses it everything but `/login` ("needs a body in the world"). Commands with no
  such check that act on the sender's own body or inventory (`/give` to oneself, say) do nothing useful for it.
- **Not counted as playing** (`PlayerRegistry.InWorld`): the status query and page, the name list sent for
  completion, invites, and `ChatManager.PlayerCount` (so the automatic update restarts under it). `/who` lists it,
  marked `(console)`; admins on it are told about a waiting release like any admin.
- **Its two RPCs must never change, and none may be added to the node**: Godot numbers a node's RPCs by name,
  and these are what two versions share, as the handshake's three. A server from before #1088 has no node: the
  client hears nothing and says so after 6 s (update that server once over SSH).
- **Client** (`Ui/ServerConsoleScreen`): opening it joins under `GameShell.PlayerName` (none yet: it says to choose
  one on the Multiplayer screen); an output pane and a command line (Up/Down history), Connect to join again.
  The link lives on its own branch, `/root/RemoteConsole<n>/Main/World` (a new name per link: the last one is
  freed a frame later, never inside its own poll, which crashed a windowed game), with its own `SceneMultiplayer`
  (the swarm bots' trick), so its paths match the server's `/root/Main/World` and no world is loaded. The
  server's other RPCs and spawns reach that peer too and are dropped with "node not found" errors in the client
  log: harmless.
- **Where:** Multiplayer screen, the console icon on every saved and LAN server row; after a refusal for the
  version (`ClientWorld.RefusedForVersion` -> `GameShell.TakeRefusedEndpoint`) the error banner's
  "Server console…" button. Keyboard to type; pad and VR reach the button, typing needs a keyboard
  (`xr/vr-action-map`). Bots: nothing (not gameplay).
- **Check:** `tools/consolelinknetcheck.sh` (net, ~30 s): a client with `--fakeprotocol 999` is refused, opens the
  console from the banner, joins as ConsoleCheck (no admin): `/update` refused, `/login wrong` and `/login test`,
  `/update` runs, `/who`, 13 s past the hello timeout, a message said to everyone, `/tp` needs a body.
  `WINDOWED=1` saves `test_output/consolelink_banner.png` and `consolelink_console.png`.
