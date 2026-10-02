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
  spectating starts with team-mates. Checked in `--brcheck` only: no loopback match in teams (two
  probes in duos make one team, which ends the match at GO). Not yet: downed-not-out, revive, team chat,
  joining as a group, boarding/jumping together.
