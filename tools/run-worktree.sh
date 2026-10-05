#!/usr/bin/env bash
# Pick a git worktree (a local feature branch), build it and run the game from it.
# Usage: tools/run-worktree.sh <godot> ["--connect 127.0.0.1"]
# Run by the VS Code task "run: game from worktree" on macOS/Linux; tools/run-worktree.ps1 is the Windows twin.
set -euo pipefail
GODOT=$1; GAME_ARGS=${2:-}

# git worktree list --porcelain: "worktree <path>" then "branch refs/heads/<name>" (or "detached")
paths=(); branches=()
while IFS= read -r line; do
  case $line in
    "worktree "*) paths+=("${line#worktree }"); branches+=("(detached)") ;;
    "branch refs/heads/"*) branches[${#branches[@]}-1]=${line#branch refs/heads/} ;;
  esac
done < <(git -C "$(dirname "$0")" worktree list --porcelain)

for i in "${!paths[@]}"; do printf '  [%d] %-32s %s\n' $((i + 1)) "${branches[$i]}" "${paths[$i]}"; done
read -rp "Worktree number: " pick
[[ $pick =~ ^[0-9]+$ ]] && (( pick >= 1 && pick <= ${#paths[@]} )) || { echo "No such worktree: $pick"; exit 1; }
tree=${paths[$pick-1]}
echo "Running ${branches[$pick-1]} from $tree"

# A worktree has no terrain of its own: terrain_chunks/ and terrain_location.json are both
# gitignored, so without this it runs on the generated fill alone — no real buildings, no church,
# and a check quietly skips half its work instead of failing. Point it at the main checkout's
# tiles, wherever its terrain_location.json says those live (tools/wt.ps1 does the same on Windows).
main=${paths[0]}
chunk_args=()
if [ ! -f "$tree/terrain_chunks/manifest.json" ] && [ ! -f "$tree/terrain_location.json" ]; then
  chunks="$main/terrain_chunks"
  if [ -f "$main/terrain_location.json" ]; then
    located=$(sed -n 's/.*"chunks"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$main/terrain_location.json")
    [ -n "$located" ] && chunks=$located
  fi
  if [ -f "$chunks/manifest.json" ]; then
    chunk_args=(--chunks "$chunks")
    echo "Terrain: $chunks (the main checkout's)"
  else
    echo "Terrain: none at $chunks; the generated fill only (no real buildings, no church)"
  fi
fi

dotnet build "$tree/UnitSportSwitzerland.csproj" /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

# A fresh worktree has no .godot/ yet: import once, headless, before the first run
[ -d "$tree/.godot" ] || "$GODOT" --headless --path "$tree" --import

# shellcheck disable=SC2086  # GAME_ARGS is split into words on purpose
if [ ${#chunk_args[@]} -gt 0 ] || [ -n "$GAME_ARGS" ]; then
  exec "$GODOT" --path "$tree" -- ${chunk_args[@]+"${chunk_args[@]}"} $GAME_ARGS
else
  exec "$GODOT" --path "$tree"
fi
