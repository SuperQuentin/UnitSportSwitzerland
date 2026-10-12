# /update: the deployed server installs the newest release (#730)

- **Only on command**: an admin (or the console, `update`) types `/update`. Nothing checks or updates on its own.
- **Check** (`Net/ServerUpdater`, `UpdateInfo.ServerUpdate`, tier 0 in `UpdateInfoTests`): GitHub's release list
  (`--updatefeed <url>` reads another, as on the client), newest stable tag against `config/version`. Up to date:
  only the admin is told. A `tools/deploy-linux.sh` build has no version and counts as older than any release.
- **Fetch while everyone plays**: the chat tells everyone the version and size, then `bash $UNITSPORT_UPDATER fetch
  <tag> <url>` (`tools/deploy/update-server.sh`, installed beside `start-server.sh`) downloads the Linux archive,
  unpacks it into `releases/<tag>` (1 GB free needed, binary present, exec bit set) and writes `pending-update`.
  A failure: its last output line goes to chat and the server keeps running. A second `/update` meanwhile is refused.
- **Restart**: once unpacked, every player is kicked ("updating to vX, update your game, reconnect in a minute"),
  the server quits 2 s later (the disconnects save the sleepers, `_ExitTree` the clock). `start-server.sh run`, the loop
  inside tmux, sees `pending-update`, runs `update-server.sh apply` (`current` -> `releases/<tag>`, last 3 kept) and
  starts the server again with the same args. Without the file it stops, as before. **The file is the signal, not the
  exit code**: a headless Godot can exit 139 (`general/headless-exit-139`).
- A fresh `start-server.sh` (deploy, reboot) deletes a leftover `pending-update`: that start picks the build.
- `UNITSPORT_UPDATER` unset (any server not started by `start-server.sh`): `/update` says it cannot update itself.
- Checked: `update-server.sh` + the tmux loop on a fake install in WSL (fetch, a segfault exit, switch, restart); in game
  on Windows with a stub script and a local feed (`UNITSPORT_UPDATER=test_output/update-stub.sh`, relative: Windows'
  `bash` on PATH can be WSL's, which cannot read a `C:/` path).
