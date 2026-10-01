# "Am I online?" is `NetLink.Online(node)` (#221)

## Rule

- To ask "connected to (or serving) other machines", call `Net/NetLink.Online(this)`. Never write
  `Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer && peer.GetConnectionStatus() == ...Connected`
  again. A node that wants a property keeps a one-liner: `private bool Online => NetLink.Online(this);`
- `NetLink.Ready(node)` (may the API be asked anything) and `NetLink.IsServer(node)` sit beside it.
- `Core/Permissions.Online` stays separate on purpose: it is static (no node) and reads the root's
  multiplayer API.

## Why

#221 PR 1: the same two lines were copied in 17 files (12 migrated) although `NetLink.Online` already existed
(issue #221 asked for a `NetworkManager.IsOnline`; adding a second helper would have been a 17th copy).

## Same logic, preserved

- Same answer for any node in the tree: a non-offline peer whose status is `Connected`.
- Outside the tree it now returns false where the copy threw (Godot's `Multiplayer` is null there).

## Migrating old code / open branches

- grep `and not OfflineMultiplayerPeer` in `src/`: replace the expression with `NetLink.Online(this)`
  (add `using UnitSport.Net;` outside that namespace).
- Not migrated yet, because open PRs edit them (do it when you next touch the file):
  `Interiors/InteriorManager` (#197), `Items/ItemEvents` (#180, #188, #201), `Loot/LootService` (#197, #201),
  `Vehicles/PassengerService` (#169), `Vehicles/VehicleManager` (#169, #233).

## How to check

`dotnet build`; any two-client script (`tools/plantcheck.sh`) still ends RESULT ok.
