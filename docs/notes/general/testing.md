# Testing: light by default, heavy only when needed (#221)

Run the cheapest tier that can catch the bug. Heavy tiers only when the change needs them.

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
- Free RAM: `Win32_OperatingSystem.FreePhysicalMemory` on Windows, `MemAvailable` on Linux.
  Light Godot runs wait for `TEST_RAM_GB` (3), tiers 2/3 for `TEST_HEAVY_RAM_GB` (6).
- Machine-wide lock for tiers 2/3: a `mkdir` lock dir, `$TEMP/unitsport-heavy.lock` (`GUARD_LOCK_DIR`),
  holding the owner's PID; a lock whose PID is dead is taken over. Two `test.sh net` started together
  ran one after the other (46 s each, 104 s in all). The lock is per OS user temp dir: Git Bash and
  WSL do not see each other's lock.
- Timeouts kill only the run's own process tree: `taskkill /T /PID <its winpid>` on Windows (the
  `_console.exe` wrapper starts the real Godot as a child), its process group on Linux. Never by name.
