# Waits in checks: wait for the thing, never a fixed time (#992)

A fixed `await Seconds(n)` costs n real seconds in every net and full run, padded for the slowest machine.
In #992, about 70 of them moved to the event they stood for (35 probes), and so did the scripts' sleeps,
polls and lingers.

## Rules for a probe (`ChatProbe` or its own helpers)

- **Settle waits become `Until(cond, T)`.** The `cond` is exactly what the next line needs or asserts, and
  the `Expect` after it stays. `T` is never shorter than the old wait: a check may get faster, never less
  patient.
- **Keep it fixed when nothing on the client can see the event:**
  - **Observation windows**: a refusal leaves no trace, a rate budget holds, "still down 5 s later".
  - **Input pacing**: holds and taps, drive and fly durations.
  - **Sampling**: `Sample`, `Watch`.
  - **Single-snapshot reads** sent to a peer: excavator arm angles, airliner N1.
  - **Settles after a teleport** (`PlaceAt`, `StandAt`, `WalkIn`, before a `Take` / `Open` / `Claim`): the
    server must have our new position, which a client cannot see.
- **A "steady state" condition is checked against what the fixed wait reached.**
  - `steamernetcheck`'s "under 2 % gained in 3 s" held at 5.3 km/h, where the fixed 30 s reaches
    14.5 km/h, so the check sampled a slow ship. The 30 s is back.
  - Compare the logged value (speed, angle) of a run before and after.
- **A wait on one peer can be what paces the other.**
  - `freighternetcheck`'s A said "ramp open" on the door bit and "parked" the moment it was out. B then
    walked up a ramp still swinging down.
  - The ramp has no state to wait on headless, so those waits stay.
- **`IsOnFloor()` right after a teleport** can still read the old ground. Wait a physics frame first, or keep
  the settle.
- **Logins:** `if (!await LogIn("pw")) return;`, which waits for `Permissions.IsAdmin`. Never `/login` then a
  pause.
- **Say-and-sleep retry loops:** `Say(x); if (await Until(answered, t)) break;` at the old interval.
- **Waits only for a screenshot:** `if (DisplayServer.GetName() != "headless")`. Headless draws no frame.
- **A peer that runs on a timeline** (thrower, driver, bridge) steps when the watcher says it saw the last
  step:
  - `radiocheck`: `RS watch <key>`.
  - `bonkcheck`: `BK victim back|lowered`.
  - The old time stays as the latest.
- **`Finish(linger)`** prints RESULT, says `finished`, and quits once every peer it heard has said `finished`
  too. The linger is the most it waits, then 0.25 s to flush. A probe with no prefix, or one that heard no
  peer, lingers as before.

## Rules for a script

- Poll every 0.2 s (`sleep 0.2`, five times the count: same timeout). `tc_server`, `guard_run` and the
  polls all do.
- **No stagger sleep** between clients whose probes already wait for each other: a hello/ready
  handshake, a gate on `Other()`.
- A late joiner waits for a log line, never a time. `clocknetcheck`'s B starts once A's sky follows the
  world clock.
- **Keep the stagger where the second client must be connected before the first acts:**
  - `racecheck`'s `--racejoin` reacts to the invite line, and chat is not replayed.
  - `crashnetcheck`'s driver crashes at t = 4 s whatever the watcher does.
- **World:** the lightest one the assertions need (`test-systems-optin`).
  - Ten scripts moved from the generated world or the real map to `--world fixture`, server and clients:
    bonk, gadgetnet, buildnet, plant, gunshot, useanim, pvp, placed, switch, radio.
  - `REAL=1` boots their old ground.

## Measured (Oct 20-21, 2026, Windows, 4.7.2, one run per side, cold tile cache)

| Scripts passing on both `main` and #992 | `main` | #992 |
|---|---|---|
| 48 net-tier scripts | 52.5 min | 47.0 min (−10.5 %) |
| 5 windowed full-tier scripts (plant, gunshot, useanim, pvp, placed) | 7.5 min | 4.4 min (−41 %) |

- **Biggest savings:**
  - buildnetcheck 75 → 32 s, gadgetnetcheck 74 → 34 s and bonkcheck 56 → 20 s (flat world, handshakes).
  - placedcheck 129 → 75 s, pvpcheck 127 → 80 s, plantcheck 63 → 33 s.
- **switchcheck** timed out on `main`'s real map (145 s) and passes on the flat world (35 s).
- **Already failing on `main`, the same on #992:** auranet, bot, brsquad (#951), crane, hold, rampalong, shop,
  sleeper, stairs, lootsync, locksync, decknet, and `--collidecheck --targets machines/` (a UFO invisible
  wall).
- **Not run on either side:** `radiocheck` and `uploadcheck` need `ffmpeg`, and this machine has none. The
  radiocheck handshake is untested.
- **Timing a script:** take the heavy lock yourself first (`guard_lock`, then `GUARD_LOCK_HELD=1 bash
  tools/x.sh`). Otherwise the time includes waiting behind another session's run, and the lock only says
  so after 2 min: one pvpcheck read 571 s.
