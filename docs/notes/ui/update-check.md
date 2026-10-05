# Update check (`Ui/UpdatePrompt`, `Core/UpdateInfo`, #532)

- Once per session the title screen asks GitHub `releases/latest` (public API, no token, 10 s timeout). If its tag is newer than `config/version` (stamped by `tools/release.sh`), a `Modal.Confirm` offers **Download** / **Later**. Later = nothing until the next launch.
- Download fetches this platform's asset (`UpdateInfo.SuffixFor`: the suffixes `tools/release.sh` uploads) with an `HttpRequest` into the OS Downloads folder (else `user://`'s folder) as `<name>.part`, renamed when complete; `Modal.Progress` shows MB and Cancel (Esc / B too). Then "Show file" opens the file manager on it (macOS: the `xattr -cr` reminder). Failure or no asset for this OS: "Open release page" in the browser.
- It does not install: the running game cannot replace itself; the player unpacks the archive.
- Never asks: development builds (no version), headless runs, command-line runs that skip the title. A reply that lands while another page covers the title is kept and offered when the title is back.
- Try it on a dev build: `<godot> --path . -- --fakeversion 0.0.1` (whitelisted in `GameShell.UseTitle`); `--uishot test_output/update.png 8` to capture it.
- Input: only `Modal` buttons, so keyboard, pad and VR pointer work like every menu; no new action.
- Tier 0: `UpdateInfoTests` (version compare, asset pick, error bodies).
