# A saved maxConcurrentBuilds of 1 makes the loader look broken

- **A saved `maxConcurrentBuilds: 1` makes the loader look broken.** A test run that loads one tile
  at a time (3.8/s) shows a world full of holes that reads like a streaming bug. Check
  `user://settings.json` first, or pass `--builds 0` (auto) to probes.
