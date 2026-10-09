#!/usr/bin/env bash
# Installed on the server as $DEPLOY_DIR/start-server.sh by tools/deploy-linux.sh (values baked in there).
# Starts the dedicated server in a detached tmux session so the operator console (stdin) stays usable:
#   tmux attach -t unitsport   (detach: Ctrl-b d)
# Run by cron @reboot and by every deploy. A no-op when the session already exists.
# `start-server.sh run` is the loop inside that session: the server, then again on the new release after
# an in-game /update fetched one (update-server.sh, #730). Any other exit ends the session, as before.
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin
DIR='@DEPLOY_DIR@'
export UNITSPORT_CHUNKS='@CHUNKS_DIR@'
export UNITSPORT_UPDATER="$DIR/update-server.sh"
SESSION=unitsport

if [ "${1-}" = run ]; then
  while :; do
    cd "$DIR/current"   # again on every pass: the symlink moves on an update
    ./UnitSportSwitzerland.x86_64 --headless -- --server --port @GAME_PORT@ @SERVER_ARGS@ || true
    # the pending file, not the exit code, says an update is waiting (a headless Godot can exit 139)
    [ -f "$DIR/pending-update" ] || break
    bash "$DIR/update-server.sh" apply || echo "update not applied, starting the same build again"
    echo "=== start $(date -Is) (after /update) ==="
  done
  exit 0
fi

tmux has-session -t "$SESSION" 2>/dev/null && { echo "already running (tmux attach -t $SESSION)"; exit 0; }
# a fetch whose server was then stopped from outside (a deploy, a reboot): that start decides the build, not it
rm -f "$DIR/pending-update"
echo "=== start $(date -Is) ===" >> "$DIR/server.log"
# The server writes straight to the pane (a tty, so its output is line-buffered and the console shows it);
# pipe-pane copies the pane to the log. Piping through tee instead block-buffers everything until exit.
tmux new-session -d -s "$SESSION" -x 200 -y 50 "bash '$DIR/start-server.sh' run"
tmux pipe-pane -t "$SESSION" -o "cat >> '$DIR/server.log'"
echo "started (tmux attach -t $SESSION, log $DIR/server.log)"
