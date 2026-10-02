# No per-frame allocations

## Rule
In `_Process`, `_PhysicsProcess` and anything they call every frame (HUD updates, probes that
run in normal play excluded):
- **StringName**: never pass a string literal or `const string` where Godot wants a
  `StringName` (`SetShaderParameter`, `GlobalShaderParameterSet`, `Input.IsActionPressed`,
  `Input.GetVector`, `EmitSignal`, `Call`, `Get`/`Set`...). Each call converts to a new
  `StringName`. Use `private static readonly StringName X = "x";`, an array of them for slots
  (`ClipEye[slot]`, never `$"portal_clip_eye_{slot}"`), or `PlayerInput`'s cached names
  (`PlayerInput.Held`/`Strength` take a string and look the `StringName` up once).
- **No LINQ, `ToList`, `ToArray`, `ToHashSet`, `string.Join`, `new List`/`new HashSet`/`new[]`**
  per frame. Keep a field list and `Clear()` it; sort with `List.Sort(static (a, b) => ...)`;
  compare sets with a kept `HashSet` instead of building a string key; `stackalloc` small
  fixed arrays of structs.
- **UI text only on change**: build into a reused `StringBuilder` (its `Append($"...")` takes an
  interpolated string without allocating one), compare with `sb.Equals(lastShown.AsSpan())`,
  and only then `ToString()` and assign `Label.Text`. Pattern: `PlayerFeel.SetText`.
- **Shader parameters and Environment properties only on change** when the value is usually
  constant (a portal quad's `live`, a door light slot, `DayNight`'s environment).
- **Input hints**: `InputHints.Label`/`Format` are memoised per (action or text, device); call
  them freely. Anything that changes the `InputMap` must call `InputHints.Invalidate()`
  (`PlayerInput.Bind` does). `Tag(action)` still concatenates: in per-frame code append
  `'['`, `Label(action)`, `']'` instead.
- Server jobs follow the same rule: return early when nobody needs the work
  (`WebRadio.Serve` with no subscriber and no tap), and re-serialise only on change
  (`QueryResponder.Refresh` compares the `ServerStatus` record, value equality).
- Something that only has to *notice* things (a group scan, a proximity search) runs at
  10 Hz with a margin, and only the exact per-frame work stays per frame (`DoorwayGhosts.Scan`).

## Why
#221 (branch `feat/221-gc-sweep`), real terrain, main vs the branch back to back:

| run (`--perflog`, t >= 25 s, vsync off) | gen0/min | gen1/min | frame p50 / p99 ms |
|---|---|---|---|
| on foot, Riddes (`--ride foot`) | 141 -> 144 | **119 -> 12** | 1.85 / 3.33 -> 1.67 / 2.78 |
| car HUD (`--ride car:0`) | 159 -> 153 | **146 -> 9** | 1.67 / 2.78 -> 1.52 / 2.38 |
| doors (`--interiorcheck`) | 150 -> 137 | **137 -> 38** | 2.08 / 3.23 -> 1.90 / 3.03 |

gen0 hardly moves (terrain streaming allocates ~2.3 GB/min and dominates it). gen1 is what
dropped: a `StringName` (and any Godot wrapper) is finalizable, so every per-frame one survives
its first gen0 and is promoted; ten a frame are enough to drive a gen1 every second.

## Same logic, preserved
- Memoised text is rebuilt from the same values every frame and compared, so nothing can go
  stale: there is no hand-written "key" of inputs to forget a field in.
- `InputHints` memo: a keyboard layout switched while the game runs keeps the old letters until
  the next `Invalidate()` (ponytail: hook a layout-change notification if that ever matters).
  `Format`'s cache is cleared past 256 entries, because some callers pass texts with numbers.
- A per-frame `List.Sort` is not stable where `OrderBy` was: only use it where ties do not
  matter (distances).

## Migrating old code / open branches
Grep your diff (not the whole tree) for offenders added in per-frame code:
```
git diff origin/main... -- src | grep -nE '^\+.*(SetShaderParameter|GlobalShaderParameterSet|IsActionPressed|GetActionStrength|GetVector|GetAxis)\("'
git diff origin/main... -- src | grep -nE '^\+.*\.(ToList|ToArray|ToHashSet|OrderBy|Where|Select)\(|string\.Join|new List<|new HashSet<'
git diff origin/main... -- src | grep -nE '^\+.*\.Text = \$"'
```
Then check whether the hit is reached every frame (`_Process`, `_PhysicsProcess`, an
`Update*` they call). One-off setup code is fine as it is.

Branches touching the files of #221 rebase like this:
- `PlayerFeel.UpdateHud` (#220, #223): the speed and engine labels are now `StringBuilder`
  appends into `_speedText`/`_engineText` + `SetText`. A new readout is one more `sb.Append`
  in the matching branch, not a `+ $"..."` term; `UpdateHint` texts are unchanged.
- `PlayerInput` (#148): new per-frame reads use `NLeft`-style `static readonly StringName`
  fields or `ActionName(action)`; keep the `InputHints.Invalidate()` at the end of `Bind`.
- `DayNight`: the environment writes are skipped while `(sky, tint, Night)` equals `_applied`;
  `SetEnvironment` sets `_applied = null` so a new environment is written at once. Code added to
  `Apply` must not go inside the `moved` blocks unless it too only changes with the palette.

## How to check
`--perflog 95` with `--ride foot,100` / `--ride car:0,100`, then the steady part of
`frames.csv`: `gc0`/`gc1` summed per minute (gen1 is the telling one) and frame p99. The
analysis script used for #221 is in that PR's body.
