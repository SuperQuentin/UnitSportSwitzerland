# Money is a counter, not an item

- **Money is a counter, not an item** (issue #32): francs added go to `Inventory.Cash` (shown by the
  hotbar and in the panel; old saves with francs in a slot are migrated on load) and are **lost when
  knocked out** (`ItemController`, on `FootPlayer.KnockedOut`'s rising edge). **Depositing** them, only at a bank's teller
  desk (#213, the loot `banks` note; withdrawals too), moves them to the account kept by `Items/Bank` at `World/Bank`: online on the server per player name in
  `user://bank/accounts.json` (the balance is sent once `ChatManager.NameAssigned` fires, because the
  name is the key and is not known on connect), offline in `user://account.json`. Cash leaves the
  pocket only when the server answers, and the server refuses a peer that is not inside a bank. The server cannot verify the amount — the inventory is the
  client's — and a name is not a password.
  The account is also the **card** in shops (#273, the loot `shops` note): the server debits it
  itself (`Bank.Charge`, no overdraft) and sends the new balance (`Bank.Report`).
