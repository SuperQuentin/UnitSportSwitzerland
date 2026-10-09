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
  `godot ... &` without a timeout. A server + clients check sources `tools/lib/twoclient.sh`
  instead, which does all of that (`twoclient-checks`).
- **Never kill by name or pattern** (`taskkill /im`, `pkill`, `Stop-Process` on a match): other
  agents run Godot at the same time. Only the PIDs you started; `guard_run` already does it.
- Network, authority or replicated state changed: `tools/test.sh net` before the PR (root `CLAUDE.md`).

## Why: the tiers and what they cost (PR #232)

Before, every check was a full Godot launch and the multiplayer checks started several windowed
clients (6-10 GB): parallel agents ran the machine out of RAM. Measured now:

| Tier | What | Command | Measured (Windows, 4.7.1) |
|---|---|---|---|
| 0 unit | `tests/UnitSportSwitzerland.Tests`: xUnit, plain .NET, **no Godot** | `tools/test.sh unit` | 72 tests in ~1 s, ~3 s with the build |
| 0.5 quick | unit + the headless checks on `--world flat` (TestWorld) or `--systems ui`: no map, no generated world | `tools/test.sh quick [area]` | most 1-25 s at 170-430 MB; `--synccheck --world flat` 56 s / 210 MB |
| 1 quick | the same runner: checks on a fixture world or course (`--world fixture`, `--chunks fixture:<course>`) | `tools/test.sh quick [area]` | `--leavecheck --world fixture` 23 s / 540 MB; `--drivecheck --chunks fixture:hairpin` 94 s / 690 MB; `--huntcheck` 10 s / 770 MB, `--lootchancecheck` 20 s (no world flags yet) |
| 2 net | quick + a headless dedicated server and a headless `--leavecheck connect` client that joins it twice, both `--world fixture` | `tools/test.sh net [area]` | `@netsmoke` 41 s, client 750 MB + server 210 MB |
| 3 full | all of the above for every area, plus the windowed two-client `tools/*check.sh` and `loadtest.sh` | `tools/test.sh full [area]` | not measured yet: needs real terrain (`CHUNKS=`) and a display |

- `GODOT` must point at the editor executable. On Windows use the full path of
  `Godot_v4.7.1-stable_mono_win64_console.exe` (the winget `godot` link hangs), see `godot-exe`.
- **The quick tier runs on game time** (`--fixed-fps 60`, #461): ~4.5x faster in all (38 -> 8.6 min
  for the 55 checks); the rules for timing in checks, `@realtime`, `TEST_REALTIME=1`: `fast-checks`.
  The measured times in the tables below are real time, from before.
- One PASS/FAIL table at the end; every log in `test_output/tests/`. Exit code 0 only if all passed.
- Verdict: the check's last `RESULT` line (`FAIL` in it fails; exit 139 at shutdown is ignored,
  see `headless-exit-139`), else its exit code. A timeout (`TEST_TIMEOUT`, 600 s) is `TIMEOUT`.
- The quick/net tiers build `UnitSportSwitzerland.csproj` first, and import once if `.godot/` is missing.

## Tiers 0.5 and 1: each check boots only what it tests (#221 part 2)

Every check in the map carries its world flags; the rules for new probes (lightest world first,
fixture courses for driving, switchable systems, migrating a probe) are in `test-systems-optin`.

- `--world flat`: `Core/TestWorld`, a 20 km box at y 0, a fixed sun, no `ChunkManager`, physics
  only, and the one probe that supports it (`--hitboxcheck`, `--synccheck`, `--ride`).
- `--systems a,b`: the client world with only those of `terrain generated traffic trains npcs
  birds physics audio network sky interiors loot occasions ui` (`Core/Systems`). Without `terrain`
  it streams the one-tile flat fixture; without `physics` the physics server is off; without `sky`
  no clock (the style's fixed sun); without `network` a connect fails. `ui` is the baseline (menus,
  HUD, chat, inventory screen are always built on a client). The server honours the fixture only.
- `--world fixture` = every system but the map and the generated fill, on `--chunks fixture:<course>`
  (`flat` by default). Courses (`Terrain/Fixture/FixtureCourse`): `flat` (three fields, a 12 m paved strip at x 130 m and a 15 % ridge from x 170 m, `--tractorcheck`, #494), `straight` (3 km),
  `hairpin` (6 legs, 15 m hairpins, 7 % down), `narrow` (4 m, a trunk every 5 m on both edges),
  `junction` (9 m road, a T and a crossroads), `verge` (two bends, 6 m of grass, then trees), `lake`
  (#299: beach, shelf, drop-off, a river, a slipway; `--watercheck`). A
  course starts at the spawn (`--at`), so `--drivecheck` runs on it unchanged.
- Absent flags: today's boot, for players and every old command line.

Measured back to back under the lock (Windows, 4.7.1 headless, no real terrain; before = the same
check with no flags, i.e. on the generated world with every system):

| Check | Before | After |
|---|---|---|
| `--synccheck` → `--world flat` | 64.5 s, 2 318 MB | 55.5 s, 210 MB |
| `--hitboxcheck` → `--world flat` | 7.9 s, 403 MB | 1.3 s, 175 MB |
| `--ride bike,20` → `--world flat` | 31.3 s, 1 306 MB | 23.4 s, 210 MB |
| `--menucheck` → `--systems ui` | 13.3 s, 887 MB | 11.9 s, 430 MB |
| `--leavecheck` → `--world fixture` | 25.9 s, 1 333 MB | 22.6 s, 537 MB |
| `--drivecheck --traffic 0` → `--chunks fixture:hairpin` | 70.7 s, 2 225 MB (straight generated road) | 93.8 s, 693 MB (2.9 km, six hairpins) |
| `@netsmoke` client / server | 48.4 s, 1 418 / 318 MB | 40.6 s, 749 / 210 MB |

`--drivecheck` with the AE86 (car 0) fails on both: "no drift car held a planned drift". The map
row uses `--car 4` (BNR32, grip). `narrow` puts the AE86 out against the trunks at 339 m.

## Tier 0: unit tests

- Pure code only. `tools/TerrainFormat` is referenced; game files with no Godot in them are linked
  by path (`<Compile Include="..\..\src\...\X.cs" Link=...>`, as `tools/BlendCheck` does), e.g.
  `src/World/TimeCommand.cs`. To test more game logic, move it out of the `Node` into plain C#
  and link the file.
- Covered: LV95 tiles and projection (swisstopo reference point, round trips; outside Switzerland
  the inverse drifts ~1.5 m), `.terr` full and coarse round trips, legacy stride 0, bad magic,
  truncation, bilinear vs mesh height, cover, holes, trees, buildings, horizon, manifest JSON,
  `/time` parsing (also under a French locale), the fixture courses (`FixtureCourseTests`: tile
  splits, valid tiles, ground under the road, hairpin grade).
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
  --cockpitcheck --tuningcheck --occasioncheck --huntcheck --origincheck --synccheck --leavecheck`,
  and with world flags `--hitboxcheck --synccheck --ride` (flat), `--menucheck` (`--systems ui`),
  `--leavecheck` and `--drivecheck` (fixture).
- Traffic lights without real data (#386): the synthetic region of `RoadGen --test-region`
  (`tools/signal-test-region`) is built and checked in tier 0 (`SignalTestRegionTests`, ~2 s:
  designed lanes, valid plans); for a look or a traffic run point the game at it
  (`--chunks test_output/signal-region --generated off`, VS Code `run: signal test region`), not
  at a Geneva copy. Not in `quick`: the game runs on it are windowed and a minute each.
- Traffic lights (#353): `net tools/signalnetcheck.sh` for `src/World/SignalNetProbe`,
  `src/Terrain/SignalLamps`, `src/Net/ClockSync` and `tools/TerrainFormat/SignalPlan`. A headless
  server (`--world fixture`) and two clients (`--systems network`) log every group's aspect
  (`SignalPlan.State` on `ClockSync.ServerNow`) every 0.5 s of server time; the script compares
  them per instant and measures each client's clock error through the machine's wall clock
  (fails over 0.1 s). No map: a crossroads plan built in code; `CHUNKS=<dir> JUNCTION=E,N` reads
  the real record nearest E,N instead (tier 3). ~45 s.

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
