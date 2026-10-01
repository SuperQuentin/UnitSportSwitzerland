# Chat and commands

- **Chat and commands** (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at
  `World/Chat` — the path must match, because Godot routes RPCs by node path. Clients only
  submit text and render replies; **every** decision (permissions, names, teleport
  destinations) is taken on the server, since a client-side permission check is one the client
  can edit. **Enter** opens the input, **/** opens it pre-filled, Up/Down walk the history.
  **Offline the chat still runs:** `ClientWorld` builds `World/Chat` and `ChatUi` at boot, and
  with no server connection (`ChatManager.IsLocal`) commands run on this machine with operator
  rights — `/help /who /me /city /spawn /occasion`; server-only ones (`/race /tp /kick`…) say
  they need a multiplayer game. **Tab** completes (`Core/ChatCompleter`, pure text in/out): command
  names, sub-commands, towns (`PlaceSearchUi.Search`), players (asked from the server with
  `RequestPlayerNames`), occasion ids, items; Shift+Tab walks back. It offers only what the
  player may run, but the server re-checks everything.
  **`/spawn <item> [count]`** (admin online, anyone offline): `Items/ItemLookup` parses it (case,
  spaces and dashes ignored, unambiguous prefix ok, count clamped); online the server validates
  then sends `GrantItem` to the sender, whose local `Inventory` takes the items.
  Check: `<godot> --headless --path . -- --chatcheck` (completion + parsing, prints a RESULT line).
