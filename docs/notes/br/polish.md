# Battle Royale polish (#231, part 6 of #177)

- **Stings** (`BrSounds`, `BrManager.Sounds`): two-voice `ChipTune` phrases, original, built once.
  - Doors open (aboard), each circle starting to close, the final one, the win (whole winning team),
    out (you). One `AudioStreamPlayer` for stings: a new one cuts the last.
  - Heartbeat (two low thumps) while the zone actually hurts you (`Dps > 0`): every 0.95 s at full
    health down to 0.5 s, louder as you weaken.
  - Whoosh on the jump (always), a heavy `ImpactBank` thud (pitch ×0.5) where a supply drop lands.
  - `--brcheck` checks they build and are short; nobody has listened to them in a test.
- **Airdrops** (`BrCrates.Beacon`): once landed, a 160 m column of translucent blue (mixed, not added:
  added light vanished against a bright sky; no fog) and a pulsing blue light. Flare drops get it too.
- **Display choices** (`BrPrefs`, `user://br/settings.json`, a file of their own): minimap Small/Medium/Large
  (170/220/290 px), minimap turns with you (drawn about its middle with `DrawSetTransform`; N moves
  round the rim), compass on/off, stings on/off. Set from buttons at the bottom right of the full map (M).
  Not in the settings screen yet: `main` reworked it (`src/Ui/SettingsScreen.cs`) after this stack branched;
  add a section there once it merges.
- **`--br`** (client): joins every lobby as its state arrives (once per seed), via `/br join` in chat.
- **Squads groundwork**: `BrState.TeamSize` (`/br open ... duos|trios|squads`, admin), `AssignTeams` at GO
  (seeded shuffle, last team short), `SideOf` / `Hostile` (no friendly fire through `PvpRules.Override`),
  `TeamsAlive` ends the match, a side places when its last member is out, the winner's whole side wins
  (`WinnerTeam`). HUD: teams alive, your team-mates; maps: green arrows with names; compass row 3;
  spectating starts with team-mates. Not yet: downed-not-out, revive, team chat, boarding/jumping together.
- **Picked teams** (#469): `/br team <name>` in the lobby (`BrEntrant.Party`, `PartyName`: trimmed, lower
  case, letters and digits, ≤ 16; no name leaves the group). `AssignTeams` puts each group in one team
  (split into several when bigger than `TeamSize`), then fills the places left with the shuffled rest,
  short teams first; without groups it is the old plain shuffle. `/br status` shows `Name [group]`.
- **One side alone plays on** (#469): `_sidesAtGo`; a match that boarded a single side (friends in one
  squad, or one player started alone) ends only when it is out or the zone has closed, not at GO.
- **Pings** (#469): `ping` action (middle mouse; middle-click on the full map). Client `PingCrosshair`
  (camera ray, 2 km) / `PingMap` → server `RequestPing` (alive, in a team, ≤ 2.5 km from the body, one
  per 1.5 s) → `Pinged` to every team member, out or not, sender included. Each client keeps one ping per
  player for 8 s (`Pings`, local clock): a pink diamond with name and distance on screen (`BrHud`,
  projected; a map ping stands on the ground where its tile is loaded), a pin on the minimap and full map
  (`BrMapDraw.Overlays`), compass row 4, `BrSounds.Ping`. Loopback: `SQUAD=1 tools/brcheck.sh`.
- **Down, not out** (#475, `Player/FootPlayer.Downed.cs`): at 0 HP in a squad match with a team-mate
  standing (`FootPlayer.CanBeDowned` ← `BrState.MateStanding`), `TryGoDown` instead of `Die`: `Down = 2`
  (replicated; hit tests skip only 1, so a downed body can be finished), flat (`BodyPose`), crawling at
  `CrawlSpeed` (no run, jump, slide), no items (`UsablePlayer`), no interact. `BleedLeft` 100 drains over
  `BleedSeconds` 30 and with every hit; at 0 `BleedOut` = the ordinary `Die` + `Died`, the kill to whoever
  downed them unless another finished them. Client `WentDown` → server `ReportDowned` (`BrEntrant.Downed`,
  `DownedBy`, feed `A downed B`). A standing team-mate holds Interact within 2.2 m for `ReviveSeconds` 5
  (`ReviveTick`, HUD bar) → `RequestRevive` (server: same team, both bodies within 3.5 m) → `Revived` →
  `ReviveInPlace` (30 HP, where they lie; feed `A revived B`). `TeamsAlive` counts only the standing; a team whose
  living are all down (`OutIfAllDown`, on going down and on any elimination) gets `OutNow` each, and their
  clients report the death as usual (death boxes included). `End` picks a standing winner. HUD: DOWN banner
  with the seconds left and a bar; mates marked "(down)" on maps and compass. Solo is unchanged.
- **Respawn tickets** (#480, `BrManager.Recall.cs`): in squads, a player who goes out before zone
  `RecallBefore` (4) while a team-mate stands leaves `ItemId.Dogtag` in the death box (`LeavesTag`; once per
  player, `BrEntrant.Recalled`). At GO (once the roads are in) the server picks `RecallStops` (4) road points
  spread over the first circle, farthest-point (`PickStops`, `BrState.RecallPoints`: x, y, alt). Clients put a
  yellow Postauto sign there (`StopSigns`) and the maps a yellow "P"; carrying a tag shows a HUD hint and the
  nearest stop on the compass (row 6). The tag used within 6 m of a stop (`ItemUse.Recall` → `TryRecall`) →
  `RequestRecall` (server: standing, in a team, ≤ 10 m from a stop, before zone 4) brings back the team-mate
  who went out last (`OutAt`): alive, unplaced; `Recalled` drops them by wingsuit 180 m over the stop with a
  knife and two bandages; `RecallNews` spends the recaller's tag and feeds "A recalled B".
