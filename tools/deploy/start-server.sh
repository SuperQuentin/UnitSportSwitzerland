#!/usr/bin/env bash
# Installed on the server as $DEPLOY_DIR/start-server.sh by tools/deploy-linux.sh (values baked in there).
# Starts the dedicated server in a detached tmux session so the operator console (stdin) stays usable:
#   tmux attach -t unitsport   (detach: Ctrl-b d)
# Run by cron @reboot and by every deploy. A no-op when the session already exists.
# `start-server.sh run` is the loop inside that session: the server, then again on the new release after
# an in-game /update fetched one (update-server.sh, #730), or on the same build after the website's dashboard asked
# for a restart (the restart-requested marker, #1211). Any other exit ends the session, as before.
# server-args.local (the dashboard's startup arguments, never written by a deploy) goes first on the command line:
# the game reads the first of a repeated option, so it wins over the deploy's.
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin
DIR='@DEPLOY_DIR@'
export UNITSPORT_CHUNKS='@CHUNKS_DIR@'
export UNITSPORT_UPDATER="$DIR/update-server.sh"
export UNITSPORT_DEPLOY_DIR="$DIR"
SESSION=unitsport
# tmux takes a bare name as a prefix: "unitsport" would match unitsport-layouts, so every lookup is exact ("=")

if [ "${1-}" = run ]; then
  while :; do
    cd "$DIR/current"   # again on every pass: the symlink moves on an update
    # read again on every pass; split on spaces, never globbed (the dashboard only saves plain words)
    LOCAL_ARGS=$([ -f "$DIR/server-args.local" ] && head -c 2000 "$DIR/server-args.local" | tr -d '
' || true)
    set -f
    # shellcheck disable=SC2086
    ./UnitSportSwitzerland.x86_64 --headless -- $LOCAL_ARGS --server --port @GAME_PORT@ @SERVER_ARGS@ || true
    set +f
    # the marker files, not the exit code, say why it quit (a headless Godot can exit 139)
    if [ -f "$DIR/pending-update" ]; then
      bash "$DIR/update-server.sh" apply || echo "update not applied, starting the same build again"
      rm -f "$DIR/restart-requested"
      echo "=== start $(date -Is) (after /update) ==="
    elif [ -f "$DIR/restart-requested" ]; then
      echo "=== start $(date -Is) (restart asked by $(head -c 64 "$DIR/restart-requested" | tr -dc 'A-Za-z0-9_ .-') on the website) ==="
      rm -f "$DIR/restart-requested"
    else
      break
    fi
  done
  exit 0
fi

# the house layout service (#1186), its own session: started here so cron and every deploy bring it up too
[ -x "$DIR/start-layouts.sh" ] && "$DIR/start-layouts.sh"
tmux has-session -t "=$SESSION" 2>/dev/null && { echo "already running (tmux attach -t $SESSION)"; exit 0; }
# a fetch whose server was then stopped from outside (a deploy, a reboot): that start decides the build, not it
rm -f "$DIR/pending-update" "$DIR/restart-requested"
echo "=== start $(date -Is) ===" >> "$DIR/server.log"
# The server writes straight to the pane (a tty, so its output is line-buffered and the console shows it);
# pipe-pane copies the pane to the log. Piping through tee instead block-buffers everything until exit.
tmux new-session -d -s "$SESSION" -x 200 -y 50 "bash '$DIR/start-server.sh' run"
tmux pipe-pane -t "=$SESSION:" -o "cat >> '$DIR/server.log'"
echo "started (tmux attach -t $SESSION, log $DIR/server.log)"
