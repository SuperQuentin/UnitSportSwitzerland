# Testing: light by default, heavy only when needed (#221)

Run the cheapest tier that can catch the bug. Heavy tiers only when the change needs them.

## Rule

- **Pure logic goes in tier 0 with a unit test.** New logic with no Godot in it (math, parsing,
  formats, rules, tables) lives in a plain C# file with no `using Godot`, linked into
  `tests/UnitSportSwitzerland.Tests/UnitSportSwitzerland.Tests.csproj`
  (`<Compile Include="..\..\src\<Area>\X.cs" Link="Game\%(Filename)%(Extension)" />`), with an xUnit test
  beside the others. Do not add a new `--xcheck` Godot flag for logic a unit test can cover.
  Never add a Godot package or `Godot.NET.Sdk` to the test project.
- **Every new check gets a `tools/lib/checkmap.txt` line**: `<path prefix> <tier> <check>`.
  The check must end by itself (quit with an exit code) and print `[name] RESULT: ok` or
  `[name] RESULT: FAILED ...` (the runner fails on `FAIL` in the last `RESULT` line). Tier
  `quick` = one headless Godot, no map; `net` = needs a server; `full` = windowed / two clients / load.
- **Heavy runs only through `tools/lib/guard.sh`**: (a script the runner calls sees `GUARD_LOCK_HELD=1`: it must not take the lock again) run a check with
  `tools/test.sh quick|net|full [area]`. A new multi-process script sources `guard.sh` and runs each
  process through `guard_run <timeout_s> <log> cmd...`, after `guard_lock` + `guard_wait_ram <GB>`
  for a server or several clients, and `guard_unlock` in its `EXIT` trap. No bare
  `godot ... &` without a timeout.
- **Never kill by name or pattern** (`taskkill /im`, `pkill`, `Stop-Process` on a match): other
  agents run Godot at the same time. Only the PIDs you started; `guard_run` already does it.
- Network, authority or replicated state changed: `tools/test.sh net` before the PR (root `CLAUDE.md`).

## Why: the tiers and what they cost (PR #232)

Before, every check was a full Godot launch and the multiplayer checks started several windowed
clients (6-10 GB): parallel agents ran the machine out of RAM. Measured now:

| Tier | What | Command | Measured (Windows, 4.7.1) |
|---|---|---|---|
| 0 unit | `tests/UnitSportSwitzerland.Tests`: xUnit, plain .NET, **no Godot** | `tools/test.sh unit` | 53 tests in ~0.1 s, ~3 s with the build |
| 1 quick | unit + the headless no-map checks the diff calls for | `tools/test.sh quick [area]` | most checks 1-5 s and ~190 MB; `--lootchancecheck` 20 s; `--huntcheck` 10 s / 770 MB; `--leavecheck` 21 s / 1.7 GB; `--synccheck` 60 s / 2.2 GB |
| 2 net | quick + a headless dedicated server (`--generated-world`) and a headless `--leavecheck connect` client that joins it twice | `tools/test.sh net [area]` | `@netsmoke` 46 s, peak 1.8 GB (server + client) |
| 3 full | all of the above for every area, plus the windowed two-client `tools/*check.sh` and `loadtest.sh` | `tools/test.sh full [area]` | not measured yet: needs real terrain (`CHUNKS=`) and a display |

- `GODOT` must point at the editor executable. On Windows use the full path of
  `Godot_v4.7.1-stable_mono_win64_console.exe` (the winget `godot` link hangs), see `godot-exe`.
- One PASS/FAIL table at the end; every log in `test_output/tests/`. Exit code 0 only if all passed.
- Verdict: the check's last `RESULT` line (`FAIL` in it fails; exit 139 at shutdown is ignored,
  see `headless-exit-139`), else its exit code. A timeout (`TEST_TIMEOUT`, 600 s) is `TIMEOUT`.
- The quick/net tiers build `UnitSportSwitzerland.csproj` first, and import once if `.godot/` is missing.

## Tier 0: unit tests

- Pure code only. `tools/TerrainFormat` is referenced; game files with no Godot in them are linked
  by path (`<Compile Include="..\..\src\...\X.cs" Link=...>`, as `tools/BlendCheck` does), e.g.
  `src/World/TimeCommand.cs`. To test more game logic, move it out of the `Node` into plain C#
  and link the file.
- Covered: LV95 tiles and projection (swisstopo reference point, round trips; outside Switzerland
  the inverse drifts ~1.5 m), `.terr` full and coarse round trips, legacy stride 0, bad magic,
  truncation, bilinear vs mesh height, cover, holes, trees, buildings, horizon, manifest JSON,
  `/time` parsing (also under a French locale).
- The game csproj excludes `tests/**`; `tests/.gdignore` keeps it out of the Godot import.

## Path-to-check map: `tools/lib/checkmap.txt`

- `<path prefix> <tier> <check>`: a prefix matched against the files changed since
  `origin/main` (`TEST_BASE`), plus uncommitted and untracked ones (`.uid` files ignored).
  `[area]` (e.g. `Loot`, `src/Player/`) replaces the diff: every row whose prefix contains it.
  `full` without an area takes every row.
- `@rpc` matches a diff that adds or removes `[Rpc`, `Rpc(`/`RpcId(`, `ReplicationConfig`,
  `MultiplayerSynchronizer` or `MultiplayerSpawner` lines: that turns the net tier on.
- Only list checks that end on their own and print a `RESULT` line or exit non-zero.
  Verified to do so headless, without a map: `--interestcheck --beatcheck --meshcheck --chatcheck
  --invcheck --lootchancecheck --setupcheck --motocheck --truckcheck --driftcheck --spincheck
  --cockpitcheck --tuningcheck --occasioncheck --huntcheck --origincheck --synccheck --leavecheck`.

## Resource guard: `tools/lib/guard.sh`

- Sourced by the runner, usable by any script: `guard_wait_ram <GB>`, `guard_lock`/`guard_unlock`,
  `guard_run <timeout_s> <log> cmd...`. Windows (Git Bash) and Linux.
- Free RAM: `/proc/meminfo` (`MemAvailable` on Linux; in Git Bash `MemFree` is Windows' free
  physical memory, read in ~30 ms), PowerShell only as a fallback. Light Godot runs wait for
  `TEST_RAM_GB` (3), tiers 2/3 for `TEST_HEAVY_RAM_GB` (6).
- **RAM watchdog, so a run can never take Windows or WSL down.** Waiting for RAM before a run is not
  enough: a run can grow during execution (WSL died several times that way). `guard_run` reads free
  RAM every `GUARD_MEM_EVERY` (2) s while its command runs. When free RAM falls under
  `GUARD_MIN_FREE_MB` (1500), it kills that command's process tree, and only it, and returns 137.
  For a process started some other way, `w=$(guard_watch <pid> [floor_mb])` watches it in the
  background; stop the watchdog with `kill $w`. **Every Godot, server, client, bot or preprocessor run
  goes through `guard_run` or under `guard_watch`.** Exit 137 means "rerun later with more free RAM",
  never "raise the floor".
- Process trees on Windows: `taskkill /T` follows Windows parentage only, and a process that Git
  Bash forked is not the Windows child of its bash. So the kill walks the Git Bash tree (`ps -ef`
  pid/ppid) and runs `taskkill /T` on each process, which also takes the real Godot under its
  `_console.exe` wrapper. The old version left a script's `sleep`, or a Godot started inside a
  script, running after a timeout.
- Machine-wide lock for tiers 2/3: a `mkdir` lock dir, `$TEMP/unitsport-heavy.lock` (`GUARD_LOCK_DIR`),
  with the owner's PID, `info` (start time, `max_hold`, what runs) and a `beat` file. Two `test.sh net`
  started together ran one after the other (46 s each, 104 s in all). The lock is per OS user temp
  dir: Git Bash and WSL do not see each other's lock.
- **Stale locks are taken over at once, never waited out.** An ad-hoc lock once kept two agents
  waiting 20 min behind an owner that had died. Now a waiter takes the lock over when:
  - the owner PID is gone (`kill -0`);
  - the heartbeat, which a helper touches every `GUARD_BEAT` (10) s and which dies with its owner, is
    older than `GUARD_STALE` (45) s. This covers a hung owner and a reused PID;
  - it has been held longer than the owner's `guard_lock <max_wait> <max_hold>` + 120 s. This covers
    a live shell that forgot to unlock.

  `tools/test.sh lock` (`guard_status`) shows who holds it, for how long, the last heartbeat and
  whether it is stale. A waiter prints the same every 2 min. `tools/lib/guard_selftest.sh` (in
  `quick` for `tools/lib/`) checks the four cases: dead, stalled heartbeat, past max, live.
  Never write your own `mkdir` lock loop: source `guard.sh`.
- Timeouts kill only the run's own process tree: `taskkill /T /PID <its winpid>` on Windows (the
  `_console.exe` wrapper starts the real Godot as a child), its process group on Linux. Never by name.

## Same logic, preserved

- The checks themselves are unchanged: the runner only launches them (`--headless --path . -- <flag>`)
  and reads their output. A check that passes by hand passes in the runner.
- The verdict is the last `RESULT` line, so a check must print it **after** everything it tests.
  Traps: a check that prints `RESULT` and then keeps running, or never quits, ends as `TIMEOUT`;
  a failure printed without the word `FAIL` in the `RESULT` line counts as a pass; a check that
  prints no `RESULT` is judged by its exit code only (exit 139 then fails).
- Unit tests link the game's own source files, never copies: a copy would test code the game no
  longer runs. A linked file that gains `using Godot` breaks the test build: move the Godot part out.
- The game csproj excludes `tests/**`; a test file there never ends up in the game assembly.
- The lock is held by the runner's PID: a crashed runner's lock is taken over, never deleted by hand
  while its PID lives.

## Migrating old code / open branches

- Rebasing a branch made before #232: no conflicts expected in `src/`. Possible conflicts:
  - root `CLAUDE.md`, the general notes list and rule 4 ("Test in multiplayer" is now "Test the
    cheapest tier"): keep both sides' lines;
  - `UnitSportSwitzerland.csproj` `DefaultItemExcludes` (now has `tests/**`): keep `tests/**` and your change;
  - `UnitSportSwitzerland.sln`: keep both project entries.
- Then, for each `--xcheck` / probe your branch added (`git diff --name-only main... | grep -E 'Probe|Check'`,
  `grep -rhoE '"--[a-z]+check"' src` against `tools/lib/checkmap.txt`): add a checkmap line for it,
  and make it print `RESULT: ok|FAILED` and quit by itself if it does not yet.
- A `tools/<x>check.sh` your branch added: list it as tier `net` (headless) or `full` (windowed) in
  the map; inside it, replace `timeout N "$GODOT" ... &` + `kill $SERVER` with `. tools/lib/guard.sh`,
  `guard_lock`, `guard_wait_ram 6` and `guard_run N <log> "$GODOT" ...` (see `netsmoke` in `tools/test.sh`).
- Pure helpers your branch added inside a `Node` class: move them to a plain file and give them a
  unit test (Rule above).
- Open PRs when #232 was opened that add checks or check scripts: #148 (`WheelProbe`, csproj),
  #169 (`DeckProbe`, `PassengerProbe`, `ExitProbe`), #180 (`PvpProbe`, `tools/pvpcheck.sh`),
  #188/#191/#197/#201/#223 (`BrCheck`, `BrProbe`, `tools/brcheck.sh`; #188 also `CLAUDE.md`),
  #219 (`BankProbe`, `LootProbe`, `tools/bankcheck.sh`), #220 (`CrashNetProbe`, `RideProbe`,
  `tools/crashnetcheck.sh`), #230 (`RoadStandProbe`, `WallOffProbe`, `TrafficProbe`),
  #233 (`tools/loadtest.sh`, already wired as a `full` row).

## How to check

- `tools/test.sh unit` (3 s): the tier-0 project builds and passes.
- `tools/test.sh quick <area>` for the area you touched; a new check shows up as a row in the table.
- Guard: start two `tools/test.sh net Net` at once; the second prints `[guard] another heavy run holds
  the lock`, and both pass one after the other.
