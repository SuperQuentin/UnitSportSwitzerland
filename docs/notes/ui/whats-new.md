# What's new (`Ui/TitleScreen`, `Core/ReleaseNotes`, #1413)

- Rides on the update check's single release-list fetch (`update-check`): `UpdatePrompt.Check()` keeps the whole list in `UpdatePrompt.Releases` and raises `Listed`, even when nothing newer exists. No second HTTP call; development builds, headless runs and phones never fetch, so they have no notes.
- `UpdateInfo.Release.Body` is the release's markdown; `UpdateInfo.NotesBetween(releases, lastSeen, current, max = 10)` picks the stable releases in (lastSeen, current], newest first.
- `ReleaseNotes.ToBbcode`/`Format` (pure, tier 0): headings bold, `- ` bullets as "•", `**bold**`, link labels without URLs, `[` `]` escaped; image lines and the `**Full diff:**` line dropped; `#1234` stays plain text.
- **Once after an update:** `GameSettings.LastSeenVersion` (saved with `SaveOnly`). When the list arrives and it differs from this build's version, it is set to the build's version; if it was an older version, `Modal.Notes` lists the releases since. A first install (empty) only records it. Offline: not recorded, so the next online session shows them. Driven by the version, so delta-patched and hand-unpacked updates behave the same.
- **On demand:** the "What's new" title entry (after Controls) shows the last 10 releases up to this build. Disabled with a tooltip until the list is in; hidden where the update check never runs.
- `Modal.Notes`: a `RichTextLabel` (focus: arrows and the pad stick scroll it) and Close; Esc / B closes. No new action, nothing for bots (UI only).
- Check by hand: `--fakeversion 0.5.0 --updatefeed URL`, with `lastSeenVersion` in `user://settings` (use `tools/wt.ps1 ... -Scratch`) set older.
