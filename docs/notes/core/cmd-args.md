# CmdArgs: one reader for the command line (#221)

## Rule

- Never call `OS.GetCmdlineUserArgs()` or hand-write `Array.IndexOf(args, "--x")` / `args[i + 1]` parses.
  Use `Core/CmdArgs` (cached once: the args never change while the game runs):
  - `CmdArgs.Has("--x")`: the flag is there (exact match).
  - `CmdArgs.Value("--x")`: the word after the first `--x`, null when absent or last. A following `--flag`
    counts as the value; pass `notFlag: true` when it must not. `at: 2` reads the second word
    (`--crashnet A <password>`).
  - `CmdArgs.Float/Double/Int("--x"[, at])`: that word parsed with InvariantCulture, null when absent or
    not a number: `CmdArgs.Float("--kmh") ?? 45f`.
  - `CmdArgs.FlagWithShot("--xcheck")`: the probe switch `--xcheck[,shot]` → `(Requested, Shot)`.
  - `CmdArgs.All`: the raw array, for loops that need every occurrence. Shared: never modify it.
- `CmdArgs` stays Godot-free apart from the `#if GODOT` block: tier 0 links it
  (`tests/UnitSportSwitzerland.Tests/CmdArgsTests.cs`) and tests the `string[]` overloads. Add a test there
  for any new reader.
- Tools (`tools/*`) are separate processes and keep their own parsing.

## Why

#221 cluster #3: 173 `GetCmdlineUserArgs()` calls in 90 files, each its own `IndexOf`, bounds check and
TryParse (some culture-dependent `int.TryParse`), and local helpers (`DriveProbe.ArgAfter`, `ArrivalProbe.Arg`,
`TruckProbe.Arg`, `StyleKit.ArgValue`, `GarageProbe.Arg`, `RideProbe.Arg`, `AvatarPreview.After` ×3,
`GameShell.Has`). Each call also allocated a new array from the engine. Net −360 lines in `src/`; no behaviour change.

## Same logic, preserved

- First occurrence wins, as `Array.IndexOf` and the `for (i < Length - 1)` loops did.
- `Value` returns `--flags` as values unless `notFlag`: callers that checked `!StartsWith("--")` pass `notFlag: true`.
- Left on `CmdArgs.All` on purpose, because a reader would change them:
  - loops where the **last** occurrence wins or every one counts (`WorldLaunch --connect/--gpx`, `DriveProbe.ParseArgs`,
    `OccasionManager --occasion`, `CdLibrary --cdfixture`, `HostedServer --parent-pid` and `InteriorProbe --doorkind`,
    which skip unparsable values), `RideProbe --ride`;
  - `double.TryParse(..., out x)` on a present but unreadable value leaves **0**, not the default (`Main` turntable
    `--view/--crank/--stride`, `--discovercheck`); `CmdArgs.Double(...) ?? default` would differ;
  - `CombatProbe`: `IndexOf(..., "paraglider") > 0` (not first);
  - arg lists copied for a relaunch (`GameShell`, `RendererRelaunch`, `XrSession`).
- `LootService --lootepoch` keeps its `TrimStart('+')`: `long.TryParse(CmdArgs.Value(...)?.TrimStart('+'), ...)`.
- Numbers: every float/double already used InvariantCulture; `int.TryParse` used the current culture with
  `NumberStyles.Integer`, identical for ASCII digits.

## Migrating old code / open branches

- Grep your branch: `grep -rn "GetCmdlineUserArgs" src`. Replace:
  - `Array.IndexOf(OS.GetCmdlineUserArgs(), "--x") >= 0` / `.Contains("--x")` → `CmdArgs.Has("--x")`;
  - `i >= 0 && i + 1 < args.Length ? args[i + 1] : null` → `CmdArgs.Value("--x")`;
  - `... && float.TryParse(args[i + 1], Float, Invariant, out v) ? v : d` → `CmdArgs.Float("--x") ?? d`;
  - the `foreach (var a in ...) if (a.StartsWith("--xcheck")) Split(',')` block → `CmdArgs.FlagWithShot("--xcheck")`.
  - Check the exceptions above before converting a loop or an `out`-parse.
- Files of open PRs left untouched (#269, #281, #308, #313, #314); migrate them after they merge:
  `BattleRoyale/BrManager.Client`, `BrProbe`, `Birds/BirdLife`, `BirdNetProbe`, `Core/ClientWorld`, `GameSettings`,
  `OriginCheck`, `OriginShifter` (`ParseMetres`), `ServerWorld`, `SpawnPoint`, `Systems`, `Gpx/VideoExporter`,
  `Interiors/PortalDemo`, `Items/BonkCheck`, `CarCdCheck`, `EconomyProbe`, `InteractCheck`, `ItemController`,
  `Net/NetSmoothProbe`, `QueryResponder`, `Swarm`, `Player/RadioSyncCheck`, `SyncProbe`, `Ui/SettingsScreen`,
  `World/NpcArrival`, `RaceManager`, `RaceNpc`.
- Merge conflicts: a `using UnitSport.Core;` line was added after the other usings of each migrated file; keep both sides'.

## How to check

`tools/test.sh unit` (CmdArgsTests), then any flag-heavy probe: `--drivecheck --chunks fixture:hairpin --car 4 --traffic 0`,
`--perflog 5`, `--server --port N`.
