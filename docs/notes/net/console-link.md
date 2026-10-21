# The server console from a game client, whatever its version (#1088)

- **Why:** the wire protocol moves several times a day and `Net/Handshake` refuses a client of another one before
  it can type anything, so an admin with a newer game could not reach an older server to `/update` it (the live
  server sat on v0.79.0 behind v0.84.0, only SSH got in).
- **Server** (`Net/ConsoleLink` at `World/ConsoleLink`): `Command(password, line)` from any peer, never through
  the handshake. The first one with the right `--admin-password` (`PlayerRegistry.PasswordMatches`) links the
  peer: `Handshake.Exempt` (no hello timeout kick), a greeting with the server's name, version, protocol and
  player count. Every line then runs as the server's own console (`ChatManager.RunConsoleCommand`, operator), and
  every line the console shows (`ChatManager.ConsoleOutput`: its replies and every broadcast) goes back to each
  linked peer through `Reply`. No password on the server: refused ("remote console is off"). A wrong one:
  answered, disconnected; three in a row lock that address out for 60 s. A link gets no player; it is not in
  the registry, so it is not counted as playing (the automatic update restarts under it).
- **Its two RPCs must never change, and none may be added to the node**: Godot numbers a node's RPCs by name,
  and these are what two versions share, as the handshake's three. A server from before #1088 has no node: the
  client hears nothing and says so after 6 s (update that server once over SSH).
- **Client** (`Ui/ServerConsoleScreen`): password (kept in memory for the run, never saved), Connect, an output
  pane and a command line (Up/Down history). The link lives on its own branch, `/root/RemoteConsole<n>/Main/World` (a new name per link: the last one is freed a frame later, never inside its own poll, which crashed a windowed game),
  with its own `SceneMultiplayer` (the swarm bots' trick), so its paths match the server's `/root/Main/World`
  and no world is loaded. The server's other broadcasts and spawns reach that peer too and are dropped with
  "node not found" errors in the client log: harmless.
- **Where:** Multiplayer screen, the console icon on every saved and LAN server row; after a refusal for the
  version (`ClientWorld.RefusedForVersion` -> `GameShell.TakeRefusedEndpoint`) the error banner's
  "Server console…" button. Keyboard to type; pad and VR reach the button, typing needs a keyboard
  (`xr/vr-action-map`). Bots: nothing (an admin tool).
- **Check:** `tools/consolelinknetcheck.sh` (net, ~1 min): a client with `--fakeprotocol 999` is refused, opens
  the console from the banner, a wrong password, the right one, `who`, 13 s past the hello timeout, `say`.
