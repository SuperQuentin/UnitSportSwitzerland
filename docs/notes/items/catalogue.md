# Item catalogue (#262)

- `Items/CatalogueUi` (layer 13, over the inventory): every item (`ItemDefs.All` minus `Photo`,
  which is nothing without its print) as `SlotButton` tiles, by category tab, with a search box
  (Enter gives one of the first match). Click = 1, right click = 10, shift-click = a full stack
  (francs: 100 / 1 000 / 10 000). The side card shows the item and +1/+10/stack buttons, cash and
  account quick buttons, and "Clear inventory" (click twice within 3 s).
- **It decides nothing:** every button sends a chat command (`/spawn`, `/money`, `/bank add`,
  `/clear`) through `ItemController.RunCommand` = `ChatManager.Send`. Offline those run locally;
  online the server checks admin exactly as for a typed command, so a client that forces the panel
  open gains nothing. `CatalogueUi.Allowed` (`!Permissions.Online || Permissions.IsAdmin`) only
  shapes the UI; the panel closes if admin is revoked while it is open (`Permissions.Changed`).
- Opened by the inventory panel's "Item catalogue" button (shown on each `Open` when allowed) or
  `/catalogue` / `/catalog` / `/items` (handled client-side in `ChatManager.Send`, `CatalogueRequested`).
- Mouse is read in the tile's `GuiInput` (before the button), so shift is the click event's own
  flag (synthetic events in probes work); `Pressed` stays for ui_accept (pad A, Enter). Pad X = 10, Y = stack.
- `SlotButton.ShowCount = false` on tiles: a tile is a kind of item, not a stack.
- Checks: `--invuicheck` step 7 (real clicks on the bread tile, screenshot `test_output/catalogue.png`
  when windowed), `--chatcheck` (offline commands), `--econcheck` (online, admin and refused).
