# Chat and commands

- **Chat and commands** (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at
  `World/Chat` — the path must match, because Godot routes RPCs by node path. Clients only
  submit text and render replies; **every** decision (permissions, names, teleport
  destinations) is taken on the server, since a client-side permission check is one the client
  can edit. **Look** (`ui/style-guide`): closed, each line floats bottom-left as outlined text (no box)
  and fades after 9 s (max 7 on screen); **Enter** opens a small glass panel (scrollback + input),
  **/** opens it pre-filled, Up/Down walk the history like a shell (the line being typed comes back past the newest; caught on the LineEdit's own GuiInput, which otherwise keeps the arrows), Esc closes.
  **Offline the chat still runs:** `ClientWorld` builds `World/Chat` and `ChatUi` at boot, and
  with no server connection (`ChatManager.IsLocal`) commands run on this machine with operator
  rights — `/help /who /me /city /spawn /occasion /time`; server-only ones (`/race /tp /kick`…) say
  they need a multiplayer game. **Tab** completes (`Core/ChatCompleter`, pure text in/out): command
  names, sub-commands, towns (`PlaceSearchUi.Search`), players (asked from the server with
  `RequestPlayerNames`, throttled to 1/s; `NamesReceived` refreshes the list), occasion ids, items,
  and player names in free text too (plain chat from 2 letters, `@name`, `/me`, `/say`, a kick
  reason). The completions float in a popup over the scrollback, right above the input (6-row window,
  command arguments beside each command, the usage line of a typed command); the first one's missing
  letters show as ghost text. Tab takes the highlighted one and cycles, Shift+Tab back, Right at the
  end takes the ghost, a click takes a row; a Tab that leaves one choice moves on to its arguments.
  It offers only what the player may run, but the server re-checks everything.
  **`/spawn <item> [count]`** (admin online, anyone offline): `Items/ItemLookup` parses it (case,
  spaces and dashes ignored, unambiguous prefix ok, count clamped); online the server validates
  then sends `GrantItem` to the sender, whose local `Inventory` takes the items.
  **Item and money admin commands (#262):** `/give <player|me> <item> [count]` (server sends
  `GrantItem` to the target), `/clear [player]` (anyone their own pack, admin someone else's;
  `ClearInventory` RPC, cash kept), `/money <amount> [player]` (cash in the pocket, negative takes;
  `GrantCash`, capped at 1 000 000), `/bank [player] [set|add|take <amount>]` (server-kept account,
  works for an offline player's name; `Bank.AdminAdjust`, then `SendBalance` if they are online).
  Amounts accept `2k`, `1.5m` (`Net/AdminArgs`, pure, unit-tested). Offline `/clear /money /bank`
  act locally and `/give` points at `/spawn`. `/catalogue` opens the item catalogue (`items/catalogue`).
  The inventory is client-local, so these are as trustworthy as `/spawn`: the server decides who may.
  Check: `<godot> --headless --path . -- --chatcheck` (completion + parsing + offline commands, prints a RESULT line).
  **`/time`** (Minecraft style; `world/day-night`): the query is answered client-side from the
  clock on screen, set/add/speed go to the server, admin only, and change it for everyone.
- **Look** (`Core/ChatUi`): floating lines sit exactly where the same lines sit in the open panel's scrollback (`AlignFeed`: inside the panel margin, just above the input; same font size and line gap in both lists, a short scrollback bottom-aligned), so opening the chat only adds the glass behind them. The open panel is see-through (`GlassPanel(0.55)`). `--chatopen [s]` opens the input after s seconds, for screenshots.
