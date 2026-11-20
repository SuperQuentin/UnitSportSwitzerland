# Chat and commands

- **Chat and commands** (`Net/ChatManager`, `Core/ChatUi`): one class runs on both sides at
  `World/Chat` — the path must match, because Godot routes RPCs by node path. Clients only
  submit text and render replies; **every** decision (permissions, names, teleport
  destinations) is taken on the server, since a client-side permission check is one the client
  can edit. **Look** (`ui/style-guide`): closed, each line floats bottom-left as outlined text (no box)
  and fades after 9 s (max 7 on screen; the outline is strong enough for snow); **Enter** opens a small glass panel (scrollback + input),
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
- **`/movespeed [<factor> | normal] [player]`** (#1485, `player/move-speed`): an admin's movement speed factor, no
  upper cap, `1` / `normal` back to normal; offline the player, online admins only (the single check), not in a
  round. The server sends it to the body's owner (`SpeedFactorSet`) or sets a bot's body (`SetMoveSpeed`).
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
  Each row carries a group (`Groups`: General, Chat, Travel, World, Activities, Concerts & media, Items & money, Admin) and a
  few words: `/help` prints one line per group of what this player may run (#1173), `/help <command>` its arguments
  and those words (`HelpFor`). `--chatcheck` fails if a command is in no group or two.
  Tab also completes `/concert` (sub-commands, cues, input protocols, xchange), `/transfer` and `/inertia`.

## Safety, moderation and the command browser (#1499)

- **Flood gate** (`ChatManager.AcceptLine`, before parsing): five lines in a burst, then a refill of
  0.6/s; going past it silences the peer for 3 s, doubling to 60 s if they keep hammering. The refusal
  warning is itself rate-limited, so an error line cannot become the flood. The server console and
  bodiless console players are exempt; bots call `Say` directly. **A test run's server has no gate**
  (`GameSettings.TestRun`, #1857): the net probes speak their steps and commands faster than the refill, and
  the gate dropped them without a word (movespeednet, sword `/pvp`, `/time`); `--chat-flood` keeps it on for a
  check of the gate over the network. Never pace a probe around it.
- **Text boundary**: `Scrub` walks code points and replaces controls, newlines and Unicode format
  characters (RTL overrides, zero-width spaces) that could reverse or hide a line; emoji survive,
  joiners and all.
  `ClampText` cuts by code point, never through a surrogate pair. Both are used by `SubmitLine`,
  `Say`, `SubmitFromConsole` and the client's `Send`.
- **Whisper**: `/w <player> <text>` (also `/whisper`, `/tell`, `/msg`) and `/r <text>`; `ChatKind.Whisper`
  (5) is violet with an `@` mark. Only the two see it; a muted player cannot whisper.
- **Mute**: `/mute <player> [minutes]` (admin, 10 min default, max 24 h) stops speech, `/me` and
  whispers; `/unmute` lets them back. The target is told, everyone sees an admin line, and the
  warning to a muted flooder is throttled.
- **Ignore**: `/ignore <player>` / `/unignore <player>` (`ChatUi`/`GameSettings.ChatIgnore`, saved)
  hides their speech, whispers, emotes and speech bubbles on this screen. Local preference only; the
  server is never told and nobody else's view changes.
- **Report**: `/report <player> [reason]` (anyone, one per 30 s) writes a timestamped block with the
  reporter, target and the last 80 broadcast lines to `user://chat-reports.log`.
- **`/help` is a panel now** (`ChatUi.ShowHelp`): grouped by `ChatCompleter.GroupNames`, searchable,
  each command clickable into the input, with its arguments and a word on what it does; Esc / B closes
  it first, then the chat. The server console and a hand-written client still get the text form
  (`ChatManager.SendHelp`). `--chathelp [word]` opens it for a screenshot (`--chatopen` first).
- **Look**: scrollback is 300 lines (a ring); `ChatTimestamps` adds a dim `HH:mm`; repeated
  System/Admin lines inside 5 s collapse to `×n`; a speaker's consecutive lines within 90 s omit the
  name; kinds carry a glyph (`· ! > @ ×`) so colour is not the only difference; mentions of your own
  name are amber. Auto-scroll happens only when already at the bottom, otherwise a `N new ↓` chip
  jumps there. `ChatFontScale`, `ChatOpacity`, `ChatTimestamps`, `ChatShowJoins` and `ChatMentions`
  are in Settings › Gameplay › Chat.
- **`--chatdemo`**: floats (or, with `--chatopen`, opens with) one line of each kind, for screenshots;
  `ChatCheck` covers the scrubber, the rune cut, the speaker lookup, the flood burst, the ignore list
  and the command browser.
