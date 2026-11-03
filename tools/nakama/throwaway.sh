# A throwaway Nakama for a check (#1121, #1123), never the stack up.sh runs: its own compose project, random keys,
# ports and volume, all deleted at exit. Source it, then call nk_up:
#   . tools/nakama/throwaway.sh; nk_up <project> <api port> <console port> <out dir>
# It sets NK_API (http://127.0.0.1:<api port>), NK_SERVER_KEY, NK_HTTP_KEY and nk() (docker compose on that
# project), and traps EXIT to save the Nakama log in <out dir>/nakama.log and remove the stack.
NK_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
_nk_rand() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }

nk_up() {
  NK_PROJECT=$1; NK_OUT=$4; mkdir -p "$NK_OUT"
  NK_ENV="$(cd "$NK_OUT" && pwd)/.env"; NK_API=http://127.0.0.1:$2
  NK_HTTP_KEY=$(_nk_rand 24); NK_SERVER_KEY=$(_nk_rand 24)
  cat > "$NK_ENV" <<EOF
POSTGRES_PASSWORD=$(_nk_rand 24)
NAKAMA_SERVER_KEY=$NK_SERVER_KEY
NAKAMA_HTTP_KEY=$NK_HTTP_KEY
NAKAMA_CONSOLE_USER=admin
NAKAMA_CONSOLE_PASSWORD=$(_nk_rand 24)
NAKAMA_ENCRYPTION_KEY=$(_nk_rand 24)
NAKAMA_PORT=$2
NAKAMA_CONSOLE_PORT=$3
NAKAMA_ADMIN_EMAILS=admin@check.example
EOF
  trap nk_down EXIT
  nk up -d --wait > "$NK_OUT/up.log" 2>&1
}

nk() { docker compose -f "$NK_DIR/docker-compose.yml" -p "$NK_PROJECT" --env-file "$NK_ENV" "$@"; }

nk_down() {
  nk logs nakama > "$NK_OUT/nakama.log" 2>&1 || true
  nk down -v > /dev/null 2>&1 || true
}
