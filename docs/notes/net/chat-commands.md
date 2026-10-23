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
  rights — `/help /who /me /city /spawn /occasion /time /seastate /water`; server-only ones (`/race /tp /kick`…) say
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
- **The scrollback keeps 80 lines** (`MaxLines`): a trimmed line leaves the tree before its `QueueFree`. Freed
  only at the end of the frame, it kept the count up, and the trim loop spun forever on line 81 (a frozen client
  growing ~100 MB/s once a 100-player match's kill feed passed 80 lines, #837). `--chatcheck` appends 100.
- **Invites (#840, `net/invites`)**: `/invites` (open to you and from you), `/accept [player|kind|#id]`,
  `/decline [...]` (no argument: the newest), `/tpa <player>` (ask to go to them) and `/tphere <player>` (ask
  them over), anyone, 20 s apart, refused in a running match or race; `/party [list] | invite <player> |
  leave | kick <player>`. Code: `Net/ChatManager.Invites.cs`. Offline they say they need a multiplayer game.
- **`/fight <player> | accept [player] | decline | leave`** (#495, `combat/fist-fight`): anyone online;
  challenges a player within 12 m to a fist fight, takes or turns down a challenge, forfeits. Offline it says
  it needs a multiplayer game.
- **`/seastate [0..1|calm|chop|storm|gamey]`** (#299, `world/water-field`): anyone asks, an admin sets it for
  everyone (sent on join and on change); offline anyone. **`/water [E N]`**: the water at an LV95 point (or
  where you are) as this side computes it, to the 0.1 mm; the server's answer and a client's own compare
  directly (`tools/watercheck.sh`).
- **Venues and getting around (#994)**: `/stadium` (alias `/venue`, anyone, offline too): bare it gives the nearest of the
  33 `StadiumCatalog` venues, how far away it is and what plays there today (`ConcertService.ShowAt`: a hosted set or tonight's
  artist and hours). `/stadium list [stadium|arena|openair]` numbers them by kind; `/stadium <name|town|#n>` goes there
  (`Terrain/Stadiums/StadiumLookup`: case and accents ignored, a town finds every venue in it (`StadiumSpec.Town`), a whole
  name wins, an ambiguous query lists its matches). Going there is a teleport like `/city` (`MayTeleport`: not out of a
  Battle Royale, not on a `--no-teleport` server unless you're an admin), to the centre spot. `/concert` acts on the venue
  nearest you, so `/stadium <name>` then `/concert host` hosts there. **`/concert join`** (#1173, anyone, offline too)
  goes to the venue where a set is hosted now (`ConcertService.HostedVenues`, the nearest if several, the others named),
  on the same terms. `/tp <E> <N>` (admin; offline anyone) jumps to
  an LV95 point (`AdminArgs.TryLv95`: space or comma, apostrophes allowed, only round Switzerland).
- **`/help` is read off the completer's table** (`ChatCompleter.Help`, the same `Commands` rows as Tab): a new command
  gets its row there and shows in Tab, its usage line and `/help` together, so it can't be forgotten in one of them.
  Each row carries a group (`Groups`: General, Travel, World, Activities, Concerts & media, Items & money, Admin) and a
  few words: `/help` prints one line per group of what this player may run (#1173), `/help <command>` its arguments
  and those words (`HelpFor`). `--chatcheck` fails if a command is in no group or two.
  Tab also completes `/concert` (sub-commands, cues, input protocols, xchange), `/transfer` and `/inertia`.
