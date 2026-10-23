#!/usr/bin/env bash
# Installed on the server as $DEPLOY_DIR/start-layouts.sh by tools/deploy-linux.sh (#1186, docs/notes/net/house-layouts.md).
# Starts the house layout service (app/LayoutService, loopback only, behind Caddy at /api/) in a detached tmux session,
# restarted in a loop if it stops. Its settings, secrets included, are layouts/layouts.env (0600, written by the deploy).
#   tmux attach -t unitsport-layouts   (detach: Ctrl-b d); log: layouts/layouts.log
# Run by start-server.sh (so by cron @reboot and every deploy). A no-op when the session already exists.
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin
L='@DEPLOY_DIR@/layouts'
SESSION=unitsport-layouts

if [ "${1-}" = run ]; then
  set -a; . "$L/layouts.env"; set +a
  cd "$L"
  while :; do
    echo "=== start $(date -Is) ==="
    "$L/app/LayoutService" || true
    sleep 5
  done
fi

[ -x "$L/app/LayoutService" ] || { echo "no layout service in $L/app (deploy with --build)"; exit 0; }
tmux has-session -t "$SESSION" 2>/dev/null && exit 0
tmux new-session -d -s "$SESSION" -x 200 -y 50 "bash '$0' run >> '$L/layouts.log' 2>&1"
echo "layout service started (tmux attach -t $SESSION, log $L/layouts.log)"
