# /update: the deployed server installs the newest release (#730, #1088)

- **On command**: an admin (in game, through `net/console-link` from a client of any version, or the console, `update`)
  types `/update`: fetch, then everyone kicked and the restart.
- **And by itself** (`Net/ServerAutoUpdate`, #1088): every 10 min (`--auto-update-minutes N`, first check 30 s after
  the start) and at once when the handshake refuses a client of a **newer** protocol (at most once a minute), the
  same check runs quietly (`quiet: true`: nothing said when up to date, a feed that is down only logged). A newer
  release is fetched while people play and waits in `ServerUpdater.ReadyTag`; the server restarts on it **only once
  nobody is connected** (bots and players joined through the console do not count), never under a player. The connected admins are told
  once per release ("vX is out and downloaded… /update restarts it now"), and an admin who joins or logs in while it
  waits too (`PlayerInfo.AdminChanged`). A refused newer client reads that the server updates itself
  (`Handshake.NewerHint`). `/update` meanwhile restarts at once: on the waiting release without a second
  download, or, during a check, as soon as it ends (`ChatManager.RestartRequested`).
- **Off** with `--no-auto-update`, on a server not started by `start-server.sh`, and on a build with no release
  version (a `tools/deploy-linux.sh` build: it stays as deployed; `/update` still works).
  `--fakeversion` pretends a version, as on the client (`ServerUpdater.RunningVersion`).
- **Check:** `tools/autoupdatenetcheck.sh` (net, ~1.5 min): a stub update script, a python-served feed that appears
  once admin A is in; A is told, a `--fakeprotocol` client reads the hint, the restart waits for A to leave.
  `UNITSPORT_BASH` names the bash that runs the script: on Windows `bash` is WSL's `System32ash.exe`, found
  before PATH, which fails where WSL is not set up (its error comes back as UTF-16, one letter then blanks).
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
