# /transfer: one player's inventory to another (#649)

- `/transfer <from> <to>` (`me` for yourself, names as `/give` finds them). Anyone may give their own
  (`/transfer me <player>`); someone else's is an admin's. Refused for a Battle Royale entrant either
  side (`RefusedInMatch`), for the same player twice, and while one from that giver is on its way.
- What moves: what `/clear` empties (every item slot, the bag slot, the cursor stack) plus the pocket's
  cash. Worn clothes stay (they are the look), and so does the bank account (`Items/Bank`, by name).
- The inventory lives on each client (`user://inventory.json`), so both must be online. The flow, all
  in `Net/ChatManager`: the server records the pending transfer by giver and sends the giver
  `HandOver(by, to)`; its client sends `HandedOver(ids, counts, data, cash)` and empties itself; the
  server (only for a transfer it asked, stacks capped at 128, counts and cash clamped) sends the
  receiver `ReceiveInventory`, whose client gives each stack through `GiveOrDrop` (what does not fit
  drops at its feet). If the receiver left meanwhile, it all goes back to the giver. A giver leaving
  drops its pending transfer.
- Check: `tools/transfercheck.sh` (net tier).
