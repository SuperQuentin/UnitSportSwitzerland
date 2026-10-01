#!/usr/bin/env bash
# Installed on the server as $DEPLOY_DIR/start-server.sh by tools/deploy-linux.sh (values baked in there).
# Starts the dedicated server in a detached tmux session so the operator console (stdin) stays usable:
#   tmux attach -t unitsport   (detach: Ctrl-b d)
# Run by cron @reboot and by every deploy. A no-op when the session already exists.
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin
DIR='@DEPLOY_DIR@'
export UNITSPORT_CHUNKS='@CHUNKS_DIR@'
SESSION=unitsport
tmux has-session -t "$SESSION" 2>/dev/null && { echo "already running (tmux attach -t $SESSION)"; exit 0; }
cd "$DIR/current"
echo "=== start $(date -Is) ===" >> "$DIR/server.log"
# The server writes straight to the pane (a tty, so its output is line-buffered and the console shows it);
# pipe-pane copies the pane to the log. Piping through tee instead block-buffers everything until exit.
tmux new-session -d -s "$SESSION" -x 200 -y 50 "./UnitSportSwitzerland.x86_64 --headless -- --server --port @GAME_PORT@ @SERVER_ARGS@"
tmux pipe-pane -t "$SESSION" -o "cat >> '$DIR/server.log'"
echo "started (tmux attach -t $SESSION, log $DIR/server.log)"
