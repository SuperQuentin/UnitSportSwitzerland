# Chat probes and the tool dispatch table (#221)

## Rule

- A multiplayer probe whose clients talk through chat lines derives from `Core/ChatProbe`. Never copy
  `Until`, `Seconds`, `Expect`, `Fail`, `Say`, `Heard`, `Shot`, `SlotOf`, `CountOf`, `Float`, the
  `Role` parser or the `Chat`/`Me` lookups into the probe again.
  - `base(items, "<log tag>", "<chat prefix>", "<png prefix>")`; `public static string? Role => RoleArg("--xcheck");`
  - `_Ready`: `_role = Role ?? "A"; if (!await Joined(150, () => <extra ready condition>)) return;` ...
    `await Finish(<seconds before quit>);`
  - override `Shot` to log the path, `EchoSay => false` to keep `Say` out of the log, `Dash => "-"`
    only to keep an existing failure line byte-identical.
- A self-check that builds no world and quits with an exit code (`--xcheck` → `int Run()`) is one
  entry in `ClientWorld.QuickChecks`. Not an `if` block in `ClientWorld._Ready`, not in `Main`.
- A tool that places the camera itself (a `--shot`/`--probe`-style run) is one `ToolRun` entry in the
  `tools` array of `ClientWorld._Ready`: `new(() => X.Requested(), ToolAnchor.AtTarget|Dropped|Own, k => new X(...))`.
  `placedByTool` is derived from that array. Never add to a hand-kept OR list again.

## Why

#221 PR 1: 8 probes lost ~430 lines of copied scaffolding; `ClientWorld`/`Main` lost a 17-term OR
list that had to match 24 hand-written `if` blocks (one forgotten term = a spawn drop fighting the
probe for the camera height).

## Same logic, preserved

- Log lines, chat prefixes, PNG names and RESULT lines are byte-identical: the `tools/*check.sh`
  scripts grep them (`placedcheck.sh` waits for `A] say planted`, i.e. the `Say` echo).
- Tool order in the array is the old `if` order: the first requested tool wins.
- `ToolAnchor.AtTarget` = the old "spectator at `--at` E,N, 1200 m"; `Dropped` = `RemoveAnchor(spectator)`;
  `Own` = the tool positions things itself (Cinema, Hitbox, Tunnel, Flight, ShotRunner).
- An entry with `Start: null` still counts for `placedByTool` but starts elsewhere (TrafficProbe:
  it needs its own camera before the items exist).
- `--interestcheck` and `--beatcheck` now go through `GameShell` (no title, `Direct`) before quitting:
  same RESULT line, same exit code.

## Migrating old code / open branches

- Conflict in `ClientWorld._Ready` near the top (`if (Array.IndexOf(scArgs, "--xcheck") >= 0) { GetTree().Quit(X.Run()); return; }`
  or `if (X.Requested) GetTree().Quit(X.Run());`): drop the block, add
  `(() => X.Requested, X.Run)` (or `(() => Has("--xcheck"), X.Run)`) to `QuickChecks`.
  Open: #188 (`BrCheck.Requested`). (`WheelProbe.CheckRequested` and `RoadPerfProbe` were migrated in the merge.)
- Conflict on `bool placedByTool = ...` or on the `if (XProbe.ParseArgs() is ...) { _spectator.Position = ...; AddChild(...); return; }`
  chain: add one `ToolRun` to `tools` instead, in the same place in the order.
- A probe of your own with `private async Task<bool> Until(` + `_heard` + `Chat?.Send($"XX {_role} ...")`:
  derive from `ChatProbe` and delete those members (grep `private async Task<bool> Until(` in `src/`).
  Still to migrate on main: `Loot/BankProbe` (#213, merged after this PR was cut; verify with `tools/bankcheck.sh`).
  Open: #188 and its BR follow-ups #191/#197/#201/#223 (`BattleRoyale/BrProbe`), #180 (`Items/PvpProbe`).
- The scratch-inventory OR list and the `if (XProbe.Role != null) AddChild(new XProbe(items))` lines
  further down are unchanged: keep adding there (#169, #180, #188 do).

## How to check

`dotnet build`, then the two-client scripts touched (`tools/plantcheck.sh`, `gunshotcheck.sh`,
`placedcheck.sh`, `useanimcheck.sh`, `lootsynccheck.sh`, `locksynccheck.sh`; on Windows with
`GODOT=<exe>`, `APPDATA=<abs>/test_output/appdata` to keep real saves untouched): every RESULT ok.
One quick check (`--interestcheck`) and one tool (`--hitboxcheck`) must still start.
