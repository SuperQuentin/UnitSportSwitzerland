# Battle Royale match (#183, part 2 of #177)

- **Node**: `src/BattleRoyale/BrManager.cs` (server) + `BrManager.Client.cs`, at `World/BattleRoyale`
  on both sides (RPCs route by path). Created in `ServerWorld` (it also feeds `InterestService.Together`
  with `br.Together`, so everyone in a running match sees everyone else in it) and in
  `ClientWorld.StartNetworking`. There is no offline mode.
- **State**: `BrState`, sent whole as JSON (`SetState`) on every change and to a joining peer
  (`SendTo`). It holds the phase, the area, seed, pace, `CountdownEnds`/`Started` (server clock,
  `ClockSync.ServerNow`), the winner and the entrants. Each `BrEntrant` has peer, name, `Team` (0 = solo),
  alive, kills, damage, place and seconds survived.
- **Phases**: `Idle -> Lobby -> Countdown -> Playing -> Ended -> Idle`, ticked in `ServerTick`.
  - Lobby: the countdown starts once 2 have joined (60 s); `/br start` shortens it to 10 s.
  - Go: waits (30 s at most) for the server's terrain lattice, for the plane's altitude. Entrants who left are
    dropped from the list. The rest get `Board()`: into the cargo plane (`plane` note). The zone's clock
    (`Started`) starts when the plane's doors close; `FlightStart` is GO.
  - Playing ends when at most 1 is alive, or when the zone has been over for 120 s.
  - Ended: results shown for 20 s, then `Release` to every entrant.
- **Client at Board** (`EnterMatch`):
  - Stores where it stood (LV95).
  - `Inventory.BeginMatch()`, then a knife + 3 bandages.
  - Sets `FootPlayer.StayDown` (an eliminated player stays down).
  - `Permissions.SetInMatch(true)`: no admin or free-roam advantage, admin or not (#425):
    - `RideUi.Open` refuses, so you ride only what you find (also no pigeon, paraglider, wingsuit from the menu).
    - The fly camera (T) is refused (walking back from it would drop the body where it flew); a player on it
      at Board is put back into its body where it stands (`ClientWorld.OnPermissionsChanged`).
    - `Permissions.AdminTools` is false: no debug menu (hidden ground is a wallhack), no item catalogue.
    - Server side, for a living entrant (`BrManager.Playing`): `/spawn` (sender), `/give`, `/money`,
      `/clear <player>` (target), `/tp` and `/bring` (either side) are refused (`ChatManager.RefusedInMatch`),
      `/tpall` skips entrants, and `VehicleManager.MayPark` refuses an admin's conjured vehicle.
  - Is carried in the plane's hold until it jumps (`plane` note).
- **Client at Release**: undoes all of that. `Inventory.EndMatch()`, `Respawn` if eliminated, then
  teleport back. Cash is never lost to a knockout in a match.
- **Inventory lending**: `BeginMatch` saves the free-roam pack, puts it aside and empties the
  slots. `Save()` is a no-op while lent, so a crash mid-match reloads the free-roam pack.
- **Death**: the owner's `FootPlayer.Died(killer, cause)` -> `ReportDeath(killer, BrOut)` to the server.
  - `Eliminate` sets the placing and credits the killer if they are a living entrant.
  - It broadcasts a chat line and the `Eliminated` RPC (the kill feed, "ELIMINATED X" / "YOU PLACED #n" banners).
  - A disconnect or `/br leave` mid-match counts as an elimination.
- **PvP**: `Combat.PvpRules.Override` while Playing allows hits only between two living entrants
  (and not team-mates once teams exist). Anyone outside the match is left to `PvpRules.Enabled`.
  `PvpRules.HitRelayed` adds up damage dealt.
- **Spectating**: once out, a `Camera3D` follows a living entrant (the killer first) from behind.
  Its node is added as a chunk anchor. Left / right (`ui_left` / `ui_right`) switch who is followed.
- **HUD** (`BrHud`, CanvasLayer 10, drawn), from top to bottom:
  - phase line (timer, alive, kills) and the area name;
  - in the plane: when the doors open / close, the jump keys, how many are aboard;
  - out-of-zone red edge + warning (only where the zone hurts, never in the plane);
  - arrow and distance to the safe zone (the next circle once shown);
  - kill feed top right (9 s);
  - armour + held gun's rounds bottom left;
  - "Spectating X";
  - results table.
- **Server flags**: `--brpace f` scales every match's timing (the loopback check runs at 0.13, short: 0.104; its 2-player zone runs at half the timetable).
  `user://br/history.json` holds the last 5 region centres.
- Not yet: teams, spectating for players who were never in the match, a warm-up in the hold during the lobby.
