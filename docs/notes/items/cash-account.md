# Money is a counter, not an item

- **Money is a counter, not an item** (issue #32): francs added go to `Inventory.Cash` (shown by the
  hotbar and in the panel; old saves with francs in a slot are migrated on load) and are **lost when
  knocked out** (`ItemController`, on `FootPlayer.KnockedOut`'s rising edge). **Depositing** them, only at a bank's teller
  desk (#213, the loot `banks` note; withdrawals too), moves them to the account kept by `Items/Bank` at `World/Bank`: online on the server per player **id** (`PlayerIds.IdOf`, #1359) in
  `user://bank/accounts.json` (the balance is sent on `PlayerIds.Identified`, which also moves an old name-keyed
  account to the id: `Bank.ClaimLegacy`; a peer not proven yet moves no money and gets "identity not confirmed"; the last name seen per id sits beside it in `account_names.json`, a fallback only), offline in `user://account.json`. Cash leaves the
  pocket only when the server answers, and the server refuses a peer that is not inside a bank. The server cannot verify the amount — the inventory is the
  client's.
  The account is also the **card** in shops (#273, the loot `shops` note): the server debits it
  itself (`Bank.Charge`, no overdraft) and sends the new balance (`Bank.Report`).
  **Rewards** (#1223, a quest's cash) are paid into the account, never the pocket a knock-out empties:
  `Bank.Credit(peer, amount, why)` on the server (offline this machine's account), which tells the owner
  through its own `Credited(amount, balance)` RPC, so a deposit waiting for its answer is never cleared.
  `Bank.Scratch` (a check's run) moves balances without writing the file.
