#!/usr/bin/env bash
# Nakama for the trainer edition (#1121, docs/notes/net/nakama.md): Postgres + Nakama in Docker.
# Usage: tools/nakama/up.sh [up|down|logs|ps]   (default up; Git Bash, WSL or the Linux server)
# up: writes .env with random keys the first time (kept after that), starts the stack and waits until it is healthy.
# down keeps the database volume; `docker compose down -v` in tools/nakama/ would delete every account and record.
set -euo pipefail
cd "$(dirname "$0")"

rand() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }

case ${1:-up} in
  up)
    if [ ! -f .env ]; then
      # one fresh key per "change-me", so no two secrets are ever the same
      while IFS= read -r line; do
        case $line in *=change-me) echo "${line%=change-me}=$(rand 24)" ;; *) echo "$line" ;; esac
      done < .env.example > .env
      echo "Wrote tools/nakama/.env with new keys (keep it: it is the only copy)"
    fi
    docker compose up -d --wait
    set -a; . ./.env; set +a
    echo "Nakama is up: API http://127.0.0.1:${NAKAMA_PORT:-7350}, console http://127.0.0.1:${NAKAMA_CONSOLE_PORT:-7351} (user $NAKAMA_CONSOLE_USER, password in .env)"
    ;;
  down) docker compose down ;;
  logs) docker compose logs -f --tail 100 ;;
  ps) docker compose ps ;;
  *) echo "Usage: $0 [up|down|logs|ps]"; exit 2 ;;
esac
